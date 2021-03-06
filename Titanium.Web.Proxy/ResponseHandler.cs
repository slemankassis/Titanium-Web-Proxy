﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Compression;
using Titanium.Web.Proxy.Shared;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy
{
    partial class ProxyServer
    {
        //Called asynchronously when a request was successfully and we received the response
        public static async Task HandleHttpSessionResponse(SessionEventArgs args)
        {
            await args.WebSession.ReceiveResponse().ConfigureAwait(false);

            try
            {
                if (!args.WebSession.Response.ResponseBodyRead)
                    args.WebSession.Response.ResponseStream = args.WebSession.ServerConnection.Stream;


                if (BeforeResponse != null && !args.WebSession.Response.ResponseLocked)
                {
                    Delegate[] invocationList = BeforeResponse.GetInvocationList();
                    Task[] handlerTasks = new Task[invocationList.Length];

                    for (int i = 0; i < invocationList.Length; i++)
                    {
                        handlerTasks[i] = ((Func<object, SessionEventArgs, Task>)invocationList[i])(null, args);
                    }

                    await Task.WhenAll(handlerTasks).ConfigureAwait(false);
                }

                args.WebSession.Response.ResponseLocked = true;

                if (args.WebSession.Response.Is100Continue)
                {
                    await WriteResponseStatus(args.WebSession.Response.HttpVersion, "100",
                            "Continue", args.Client.ClientStreamWriter);
                    await args.Client.ClientStreamWriter.WriteLineAsync();
                }
                else if (args.WebSession.Response.ExpectationFailed)
                {
                    await WriteResponseStatus(args.WebSession.Response.HttpVersion, "417",
                            "Expectation Failed", args.Client.ClientStreamWriter);
                    await args.Client.ClientStreamWriter.WriteLineAsync();
                }

                await WriteResponseStatus(args.WebSession.Response.HttpVersion, args.WebSession.Response.ResponseStatusCode,
                              args.WebSession.Response.ResponseStatusDescription, args.Client.ClientStreamWriter);

                if (args.WebSession.Response.ResponseBodyRead)
                {
                    var isChunked = args.WebSession.Response.IsChunked;
                    var contentEncoding = args.WebSession.Response.ContentEncoding;

                    if (contentEncoding != null)
                    {
                        args.WebSession.Response.ResponseBody = await GetCompressedResponseBody(contentEncoding, args.WebSession.Response.ResponseBody).ConfigureAwait(false);

                        if (isChunked == false)
                            args.WebSession.Response.ContentLength = args.WebSession.Response.ResponseBody.Length;
                        else
                            args.WebSession.Response.ContentLength = -1;
                    }

                    await WriteResponseHeaders(args.Client.ClientStreamWriter, args.WebSession.Response.ResponseHeaders).ConfigureAwait(false);
                    await WriteResponseBody(args.Client.ClientStream, args.WebSession.Response.ResponseBody, isChunked).ConfigureAwait(false);
                }
                else
                {
                    await WriteResponseHeaders(args.Client.ClientStreamWriter, args.WebSession.Response.ResponseHeaders);

                    if (args.WebSession.Response.IsChunked || args.WebSession.Response.ContentLength > 0 ||
                       (args.WebSession.Response.HttpVersion.Major == 1 && args.WebSession.Response.HttpVersion.Minor == 0))
                        await WriteResponseBody(args.WebSession.ServerConnection.StreamReader, args.Client.ClientStream, args.WebSession.Response.IsChunked, args.WebSession.Response.ContentLength).ConfigureAwait(false);
                }

                await args.Client.ClientStream.FlushAsync();

            }
            catch
            {
                Dispose(args.Client.TcpClient, args.Client.ClientStream, args.Client.ClientStreamReader, args.Client.ClientStreamWriter, args);
            }
            finally
            {
                args.Dispose();
            }
        }

        private static async Task<byte[]> GetCompressedResponseBody(string encodingType, byte[] responseBodyStream)
        {
            var compressionFactory = new CompressionFactory();
            var compressor = compressionFactory.Create(encodingType);
            return await compressor.Compress(responseBodyStream).ConfigureAwait(false);
        }


        private static async Task WriteResponseStatus(Version version, string code, string description,
            StreamWriter responseWriter)
        {
            await responseWriter.WriteLineAsync(string.Format("HTTP/{0}.{1} {2} {3}", version.Major, version.Minor, code, description));
        }

        private static async Task WriteResponseHeaders(StreamWriter responseWriter, List<HttpHeader> headers)
        {
            if (headers != null)
            {
                FixResponseProxyHeaders(headers);

                foreach (var header in headers)
                {
                    await responseWriter.WriteLineAsync(header.ToString());
                }
            }

            await responseWriter.WriteLineAsync();
            await responseWriter.FlushAsync();
        }
        private static void FixResponseProxyHeaders(List<HttpHeader> headers)
        {
            //If proxy-connection close was returned inform to close the connection
            var proxyHeader = headers.FirstOrDefault(x => x.Name.ToLower() == "proxy-connection");
            var connectionHeader = headers.FirstOrDefault(x => x.Name.ToLower() == "connection");

            if (proxyHeader != null)
                if (connectionHeader == null)
                {
                    headers.Add(new HttpHeader("connection", proxyHeader.Value));
                }
                else
                {
                    connectionHeader.Value = proxyHeader.Value;
                }

            headers.RemoveAll(x => x.Name.ToLower() == "proxy-connection");
        }

        private static async Task WriteResponseBody(Stream clientStream, byte[] data, bool isChunked)
        {
            if (!isChunked)
            {
                await clientStream.WriteAsync(data, 0, data.Length).ConfigureAwait(false);
            }
            else
                await WriteResponseBodyChunked(data, clientStream).ConfigureAwait(false);
        }

        private static async Task WriteResponseBody(CustomBinaryReader inStreamReader, Stream outStream, bool isChunked, long ContentLength)
        {
            if (!isChunked)
            {
                //http 1.0
                if (ContentLength == -1)
                    ContentLength = long.MaxValue;

                int bytesToRead = Constants.BUFFER_SIZE;

                if (ContentLength < Constants.BUFFER_SIZE)
                    bytesToRead = (int)ContentLength;

                var buffer = new byte[Constants.BUFFER_SIZE];

                var bytesRead = 0;
                var totalBytesRead = 0;

                while ((bytesRead += await inStreamReader.BaseStream.ReadAsync(buffer, 0, bytesToRead).ConfigureAwait(false)) > 0)
                {
                    await outStream.WriteAsync(buffer, 0, bytesRead).ConfigureAwait(false);
                    totalBytesRead += bytesRead;

                    if (totalBytesRead == ContentLength)
                        break;

                    bytesRead = 0;
                    var remainingBytes = (ContentLength - totalBytesRead);
                    bytesToRead = remainingBytes > (long)Constants.BUFFER_SIZE ? Constants.BUFFER_SIZE : (int)remainingBytes;
                }
            }
            else
                await WriteResponseBodyChunked(inStreamReader, outStream).ConfigureAwait(false);
        }

        //Send chunked response
        private static async Task WriteResponseBodyChunked(CustomBinaryReader inStreamReader, Stream outStream)
        {
            while (true)
            {
                var chunkHead = await inStreamReader.ReadLineAsync().ConfigureAwait(false);
                var chunkSize = int.Parse(chunkHead, NumberStyles.HexNumber);

                if (chunkSize != 0)
                {
                    var buffer = await inStreamReader.ReadBytesAsync(chunkSize).ConfigureAwait(false);

                    var chunkHeadBytes = Encoding.ASCII.GetBytes(chunkSize.ToString("x2"));

                    await outStream.WriteAsync(chunkHeadBytes, 0, chunkHeadBytes.Length).ConfigureAwait(false);
                    await outStream.WriteAsync(Constants.NewLineBytes, 0, Constants.NewLineBytes.Length).ConfigureAwait(false);

                    await outStream.WriteAsync(buffer, 0, chunkSize).ConfigureAwait(false);
                    await outStream.WriteAsync(Constants.NewLineBytes, 0, Constants.NewLineBytes.Length).ConfigureAwait(false);

                    await inStreamReader.ReadLineAsync().ConfigureAwait(false);
                }
                else
                {
                    await inStreamReader.ReadLineAsync().ConfigureAwait(false);
                    await outStream.WriteAsync(Constants.ChunkEnd, 0, Constants.ChunkEnd.Length).ConfigureAwait(false);
                    break;
                }
            }
        }

        private static async Task WriteResponseBodyChunked(byte[] data, Stream outStream)
        {
            var chunkHead = Encoding.ASCII.GetBytes(data.Length.ToString("x2"));

            await outStream.WriteAsync(chunkHead, 0, chunkHead.Length).ConfigureAwait(false);
            await outStream.WriteAsync(Constants.NewLineBytes, 0, Constants.NewLineBytes.Length).ConfigureAwait(false);
            await outStream.WriteAsync(data, 0, data.Length).ConfigureAwait(false);
            await outStream.WriteAsync(Constants.NewLineBytes, 0, Constants.NewLineBytes.Length).ConfigureAwait(false);

            await outStream.WriteAsync(Constants.ChunkEnd, 0, Constants.ChunkEnd.Length).ConfigureAwait(false);
        }


        private static void Dispose(TcpClient client, IDisposable clientStream, IDisposable clientStreamReader,
            IDisposable clientStreamWriter, IDisposable args)
        {
            if (args != null)
                args.Dispose();

            if (clientStreamReader != null)
                clientStreamReader.Dispose();

            if (clientStreamWriter != null)
                clientStreamWriter.Dispose();

            if (clientStream != null)
                clientStream.Dispose();

            if (client != null)
                client.Close();
        }
    }
}