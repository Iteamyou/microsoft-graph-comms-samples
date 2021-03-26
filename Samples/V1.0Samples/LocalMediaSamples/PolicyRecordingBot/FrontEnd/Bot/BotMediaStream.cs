// --------------------------------------------------------------------------------------------------------------------
// <copyright file="BotMediaStream.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.
// </copyright>
// <summary>
//   The bot media stream.
// </summary>
// --------------------------------------------------------------------------------------------------------------------

namespace Sample.PolicyRecordingBot.FrontEnd.Bot
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Runtime.InteropServices;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Graph.Communications.Calls.Media;
    using Microsoft.Graph.Communications.Common;
    using Microsoft.Graph.Communications.Common.Telemetry;
    using Microsoft.Skype.Bots.Media;
    using Microsoft.Skype.Internal.Media.Services.Common;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using WebSocket4Net;

    /// <summary>
    /// Class responsible for streaming audio and video.
    /// </summary>
    public class BotMediaStream : ObjectRootDisposable
    {
        //private const string HostUrl = "https://iat-api-sg.xf-yun.com/v2/iat";
        private const string HostUrl = "https://orchestrate-ws-api-sg.xf-yun.com/v1/private/simultaneous_interpretation";
        private const string mtHostUrl = "https://its-api-sg.xf-yun.com/v2/its";
        private const string Appid = "g99e7d33";
        private const string ApiSecret = "be608559a61d64c1211ae9fdfb323d4b";
        private const string ApiKey = "8cabaa7abd3e3d45927ec3e28456ad83";
        //private const string HostUrl = "https://iat-api.xfyun.cn/v2/iat";
        //private const string Appid = "5f41ff75";
        //private const string ApiSecret = "f91aefd69fb0a31116426d3ad1c6a763";
        //private const string ApiKey = "a0db66c2087537b5273d2fc0673e2bbc";
        private enum Status
        {
            FirstFrame = 0,
            ContinueFrame = 1,
            LastFrame = 2
        }

        private WebSocket webSocket;
        private static HttpClient httpClient;
        private bool IsBufferSize;
        private static byte[] retrievedBuffer;
        private Status status = Status.FirstFrame;
        private readonly IAudioSocket audioSocket;
        private readonly IVideoSocket vbssSocket;
        private readonly List<IVideoSocket> videoSockets;
        private readonly ILocalMediaSession mediaSession;
        private readonly string callId;

        //private readonly ManualResetEvent ExitEvent = new ManualResetEvent(false);

        /// <summary>
        /// Initializes a new instance of the <see cref="BotMediaStream"/> class.
        /// </summary>
        /// <param name="mediaSession">The media session.</param>
        /// <param name="callId">call id</param>
        /// <param name="logger">Graph logger.</param>
        /// <exception cref="InvalidOperationException">Throws when no audio socket is passed in.</exception>
        public BotMediaStream(ILocalMediaSession mediaSession, string callId, IGraphLogger logger)
            : base(logger)
        {
            ArgumentVerifier.ThrowOnNullArgument(mediaSession, nameof(mediaSession));
            ArgumentVerifier.ThrowOnNullArgument(logger, nameof(logger));

            this.callId = callId;
            this.mediaSession = mediaSession;
            status = Status.FirstFrame;
            IsBufferSize = false;

            // Subscribe to the audio media.
            this.audioSocket = mediaSession.AudioSocket;
            if (this.audioSocket == null)
            {
                throw new InvalidOperationException("A mediaSession needs to have at least an audioSocket");
            }

            this.audioSocket.AudioMediaReceived += this.OnAudioMediaReceived;

            // Subscribe to the video media.
            this.videoSockets = this.mediaSession.VideoSockets?.ToList();
            if (this.videoSockets?.Any() == true)
            {
                this.videoSockets.ForEach(videoSocket => videoSocket.VideoMediaReceived += this.OnVideoMediaReceived);
            }

            // Subscribe to the VBSS media.
            this.vbssSocket = this.mediaSession.VbssSocket;
            if (this.vbssSocket != null)
            {
                this.mediaSession.VbssSocket.VideoMediaReceived += this.OnVbssMediaReceived;
            }

            using (var sw = new StreamWriter("output.txt", append: false))
            {
                sw.Write(string.Empty);
            }
        }

        /// <summary>
        /// Subscription for video and vbss.
        /// </summary>
        /// <param name="mediaType">vbss or video.</param>
        /// <param name="mediaSourceId">The video source Id.</param>
        /// <param name="videoResolution">The preferred video resolution.</param>
        /// <param name="socketId">Socket id requesting the video. For vbss it is always 0.</param>
        public void Subscribe(MediaType mediaType, uint mediaSourceId, VideoResolution videoResolution, uint socketId = 0)
        {
            try
            {
                this.ValidateSubscriptionMediaType(mediaType);

                this.GraphLogger.Info($"Subscribing to the video source: {mediaSourceId} on socket: {socketId} with the preferred resolution: {videoResolution} and mediaType: {mediaType}");
                if (mediaType == MediaType.Vbss)
                {
                    if (this.vbssSocket == null)
                    {
                        this.GraphLogger.Warn($"vbss socket not initialized");
                    }
                    else
                    {
                        this.vbssSocket.Subscribe(videoResolution, mediaSourceId);
                    }
                }
                else if (mediaType == MediaType.Video)
                {
                    if (this.videoSockets == null)
                    {
                        this.GraphLogger.Warn($"video sockets were not created");
                    }
                    else
                    {
                        this.videoSockets[(int)socketId].Subscribe(videoResolution, mediaSourceId);
                    }
                }
            }
            catch (Exception ex)
            {
                this.GraphLogger.Error(ex, $"Video Subscription failed for the socket: {socketId} and MediaSourceId: {mediaSourceId} with exception");
            }
        }

        /// <summary>
        /// Unsubscribe to video.
        /// </summary>
        /// <param name="mediaType">vbss or video.</param>
        /// <param name="socketId">Socket id. For vbss it is always 0.</param>
        public void Unsubscribe(MediaType mediaType, uint socketId = 0)
        {
            try
            {
                this.ValidateSubscriptionMediaType(mediaType);

                this.GraphLogger.Info($"Unsubscribing to video for the socket: {socketId} and mediaType: {mediaType}");

                if (mediaType == MediaType.Vbss)
                {
                    this.vbssSocket?.Unsubscribe();
                }
                else if (mediaType == MediaType.Video)
                {
                    this.videoSockets[(int)socketId]?.Unsubscribe();
                }
            }
            catch (Exception ex)
            {
                this.GraphLogger.Error(ex, $"Unsubscribing to video failed for the socket: {socketId} with exception");
            }
        }

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            this.audioSocket.AudioMediaReceived -= this.OnAudioMediaReceived;

            if (this.videoSockets?.Any() == true)
            {
                this.videoSockets.ForEach(videoSocket => videoSocket.VideoMediaReceived -= this.OnVideoMediaReceived);
            }

            // Subscribe to the VBSS media.
            if (this.vbssSocket != null)
            {
                this.mediaSession.VbssSocket.VideoMediaReceived -= this.OnVbssMediaReceived;
            }

            //if (webSocket != null)
            //{
            //    webSocket.Dispose();
            //}
        }

        /// <summary>
        /// Ensure media type is video or VBSS.
        /// </summary>
        /// <param name="mediaType">Media type to validate.</param>
        private void ValidateSubscriptionMediaType(MediaType mediaType)
        {
            if (mediaType != MediaType.Vbss && mediaType != MediaType.Video)
            {
                throw new ArgumentOutOfRangeException($"Invalid mediaType: {mediaType}");
            }
        }

        /// <summary>
        /// Receive audio from subscribed participant.
        /// </summary>
        /// <param name="sender">
        /// The sender.
        /// </param>
        /// <param name="e">
        /// The audio media received arguments.
        /// </param>
        private void OnAudioMediaReceived(object sender, AudioMediaReceivedEventArgs e)
        {
            this.GraphLogger.Info($"Received Audio: [VideoMediaReceivedEventArgs(Data=<{e.Buffer.Data.ToString()}>, Length={e.Buffer.Length}, Timestamp={e.Buffer.Timestamp})]");
            Task.Delay(40);
            // TBD: Policy Recording bots can record the Audio here
            try
            {
                /*
                using (var fs = new FileStream(this.callId + ".wav", FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    using (var bw = new BinaryWriter(fs))
                    {
                        foreach (var bytes in retrievedBuffer)
                        {
                            bw.Write(bytes);
                        }
                    }
                }*/


                int length = (int)e.Buffer.Length;
                if (retrievedBuffer == null)
                {
                    retrievedBuffer = new byte[length * 2];
                }
                //var buffer = new byte[640];
                //Marshal.Copy(e.Buffer.Data, buffer, 0, (int)length);

                if (!IsBufferSize)
                {
                    Marshal.Copy(e.Buffer.Data, retrievedBuffer, 0, length);
                    IsBufferSize = true;
                }
                else
                {
                    Marshal.Copy(e.Buffer.Data, retrievedBuffer, length, length);
                    using (var fs = new FileStream(this.callId + ".wav", FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    {
                        using (var bw = new BinaryWriter(fs))
                        {
                            foreach (var bytes in retrievedBuffer)
                            {
                                bw.Write(bytes);
                            }
                        }
                    }
                    IsBufferSize = false;

                    switch (this.status)
                    {
                        case Status.FirstFrame:
                            {
                                dynamic frame = new JObject();
                                frame.header = new JObject
                        {
                            { "app_id", Appid },
                            { "status", 0 },
                            { "uid", string.Empty },
                        };
                                frame.parameter = new JObject { };
                                dynamic ist = new JObject
                                {
                                    { "accent", "mandarin" },
                                    { "domain", "ist_open" },
                                    { "language", "en_us" },  //zh_cn
                                    { "vto", 15000 },
                                    { "eos", 150000 },
                                };
                                dynamic output_streamtrans = new JObject { { "encoding", "utf8" }, { "format", "json" }, };
                                dynamic streamtrans = new JObject
                                {
                                    { "from", "en" },
                                    { "to", "cn" },
                                };
                                streamtrans["output_streamtrans"] = output_streamtrans;
                                frame.parameter["ist"] = ist;
                                frame.parameter["streamtrans"] = streamtrans;

                                dynamic data = new JObject
                        {
                            { "status", (int)Status.FirstFrame },
                            { "sample_rate", 16000 },
                            { "channels", 1 },
                            { "bit_depth", 16 },
                            { "encoding", "raw" },
                            { "audio", Convert.ToBase64String(retrievedBuffer) },
                        };
                                frame.payload = new JObject { };
                                frame.payload["data"] = data;

                                if (webSocket == null || webSocket.State == WebSocketState.Closed)
                                {
                                    if (webSocket != null)
                                    {
                                        webSocket.Dispose();
                                    }
                                    string uri = GetAuthUrl(HostUrl, ApiKey, ApiSecret, "ws");
                                    webSocket = new WebSocket(uri)
                                    {
                                        EnableAutoSendPing = true,
                                        AutoSendPingInterval = 5,
                                    };
                                    webSocket.Closed += OnClosed;
                                    webSocket.Error += OnError;
                                    webSocket.MessageReceived += OnMessageReceived;
                                    webSocket.Open();
                                    while (webSocket.State == WebSocketState.Connecting) { };
                                }

                                webSocket.Send(frame.ToString());
                                this.status = Status.ContinueFrame;
                            }

                            break;
                        case Status.ContinueFrame:
                            {
                                dynamic frame = new JObject();
                                frame.header = new JObject
                                {
                                    { "status", (int)Status.ContinueFrame },
                                    { "app_id", Appid},
                                };
                                frame.payload = new JObject { };
                                dynamic data = new JObject
                        {
                            { "status", (int)Status.ContinueFrame },
                            { "sample_rate", 16000 },
                            { "encoding", "raw" },
                            { "audio", Convert.ToBase64String(retrievedBuffer) },
                        };
                                frame.payload["data"] = data;
                                if (webSocket.State == WebSocketState.Open)
                                {
                                    webSocket.Send(frame.ToString());
                                    //if (e.Buffer.IsSilence == true)
                                    //{
                                    //    status = Status.LastFrame;
                                    //}
                                }
                                else
                                {
                                    status = Status.FirstFrame;
                                }
                            }

                            break;
                        case Status.LastFrame:
                            {
                                dynamic frame = new JObject();
                                frame.header = new JObject
                        {
                            { "status", (int)Status.LastFrame },
                            { "app_id", Appid },
                        };
                                frame.payload = new JObject { };
                                dynamic data = new JObject
                        {
                            { "status", (int)Status.LastFrame },
                            { "sample_rate", "16000"},
                            { "encoding", "raw" },
                            { "audio", Convert.ToBase64String(retrievedBuffer) },
                        };
                                frame.payload["data"] = data;
                                if (webSocket.State == WebSocketState.Open)
                                {
                                    webSocket.Send(frame.ToString());
                                }

                                status = Status.FirstFrame;
                            }

                            break;
                        default:
                            break;
                    }
                    retrievedBuffer = new byte[length * 2];
                }
                //else
                //{
                //    switch (this.status)
                //    {
                //        case Status.FirstFrame:

                //            break;
                //        case Status.ContinueFrame:
                //            {
                //                dynamic frame = new JObject();
                //                frame.data = new JObject
                //        {
                //            { "status", (int)Status.ContinueFrame },
                //            { "format", "audio/L16;rate=16000" },
                //            { "encoding", "raw" },
                //            { "audio", Convert.ToBase64String(retrievedBuffer) },
                //        };
                //                webSocket.Send(frame.ToString());

                //        //        frame = new JObject();
                //        //        frame.data = new JObject
                //        //{
                //        //    { "status", (int)Status.LastFrame },
                //        //    { "format", "audio/L16;rate=16000" },
                //        //    { "encoding", "raw" },
                //        //    { "audio", Convert.ToBase64String(retrievedBuffer) },
                //        //};
                //        //        webSocket.Send(frame.ToString());
                //        //        this.status = Status.FirstFrame;
                //            }

                //            break;
                //        case Status.LastFrame:
                //            {
                //                dynamic frame = new JObject();
                //                frame.data = new JObject
                //        {
                //            { "status", (int)Status.LastFrame },
                //            { "format", "audio/L16;rate=16000" },
                //            { "encoding", "raw" },
                //            { "audio", Convert.ToBase64String(retrievedBuffer) },
                //        };
                //                webSocket.Send(frame.ToString());
                //                //this.status = Status.FirstFrame;
                //            }

                //            break;
                //        default:
                //            break;
                //    }
                //}
            }
            catch (Exception ex)
            {
                this.GraphLogger.Info($"got an exception while extracting media data from unmanaged array, {ex.Message.ToString()}");
            }

            e.Buffer.Dispose();
        }


        //private void OnReconnected(ReconnectionInfo type)
        //{
        //    Console.WriteLine($"Reconnection happened, type: {type}");
        //}

        //private void OnDisconnected(DisconnectionInfo info)
        //{
        //    Console.WriteLine($"Disconnection happened, type: {info.Type}");
        //    //status = Status.FirstFrame;
        //}

        //private void Connect()
        //{
        //    if (webSocket == null)
        //    {
        //        string uri = GetAuthUrl(HostUrl, ApiKey, ApiSecret);
        //        webSocket = new WebSocket(uri);
        //        webSocket.Error += OnError;
        //        webSocket.MessageReceived += OnMessageReceived;
        //        webSocket.Closed += OnClosed;
        //    }

        //    if (webSocket.State == WebSocketState.Closed || webSocket.State == WebSocketState.None)
        //    {
        //        webSocket.Open();
        //        while (webSocket.State == WebSocketState.Connecting)
        //        { }
        //    }
        //}

        //private void Close()
        //{
        //    webSocket.Close();
        //    while (webSocket.State == WebSocketState.Closing)
        //    { }
        //}

        private void OnError(object sender, SuperSocket.ClientEngine.ErrorEventArgs e)
        {
            //Console.WriteLine("OnError");
            //Console.WriteLine(e.Exception.Message);
            using (var sw = new StreamWriter("error.txt", append: true))
            {
                sw.WriteLine(e.Exception.Message);
            }

            status = Status.FirstFrame;
            //try
            //{
            //    webSocket = null;
            //}
            //catch(Exception ex)
            //{
            //    Console.WriteLine(ex.Message);
            //}
        }

        private void OnClosed(object sender, EventArgs e)
        {
            Console.WriteLine(e.ToString());
            status = Status.FirstFrame;
            //try
            //{
            //    webSocket = null;
            //}
            //catch(Exception ex)
            //{
            //    Console.WriteLine(ex.Message);
            //}
        }

        private void OnMessageReceived(object sender, MessageReceivedEventArgs e)
        {
            Console.WriteLine("OnMessageReceived");
            using (var sw = new StreamWriter("receive.txt", append: true))
            {
                sw.WriteLine(e.Message);
            }
            dynamic msg = JsonConvert.DeserializeObject(e.Message);
            if (msg.header.code != 0)
            {
                Console.WriteLine($"error => {msg.header.message},sid => {msg.header.sid}");
                return;
            }

            //var ws = msg.payload.output_streamtrans.text;
            var ws = msg.payload;
            if (ws == null)
            {
                return;
            }

            using (var sw = new StreamWriter("output.txt", append: true))
            {
                var result = JsonConvert.DeserializeObject<JObject>(Encoding.UTF8.GetString(Convert.FromBase64String(ws.output_streamtrans.text.ToString())));

                if (!string.IsNullOrEmpty(result.src.ToString()))
                {
                    //    if (msg.data.result.ls)
                    //    {
                    //        sw.Write(DateTime.UtcNow.AddHours(8).ToString("[yyyy-MM-dd HH:mm:ss]") + " " + sb.ToString());
                    //        var transResult = Translate(sb.ToString());
                    //        dynamic tResult = JsonConvert.DeserializeObject(transResult);
                    //        if (tResult.code == 0)
                    //        {
                    //            sw.Write(tResult.data.result.trans_result.dst);
                    //        }
                    //    }
                    //    else
                    //    {
                    sw.WriteLine(DateTime.UtcNow.AddHours(8).ToString("[yyyy-MM-dd HH:mm:ss]") + " " + result.src.ToString());
                    sw.WriteLine(DateTime.UtcNow.AddHours(8).ToString("[yyyy-MM-dd HH:mm:ss]") + " " + result.dst.ToString());
                    //var transResult = Translate(sb.ToString());
                    //dynamic tResult = JsonConvert.DeserializeObject(transResult);
                    //if (tResult.code == 0)
                    //{
                    //    sw.WriteLine(tResult.data.result.trans_result.dst);
                    //}
                    //}
                }
            }

            if (msg.header.status == 2)
            {
                Console.WriteLine("识别结束");
                status = Status.FirstFrame;
            }
        }

        private string Translate(string input)
        {
            dynamic requestContent = new JObject();
            requestContent.common = new JObject()
            {
                { "app_id", Appid },
            };
            requestContent.business = new JObject()
            {
                { "from", "cn" },
                { "to", "en"},
            };
            requestContent.data = new JObject()
            {
                { "text", Convert.ToBase64String(Encoding.UTF8.GetBytes(input)) },
            };

            GetITSAuthUrl(mtHostUrl, ApiKey, ApiSecret, "https", requestContent.ToString());

            var response = httpClient.PostAsync(string.Empty, new StringContent(requestContent.ToString(), Encoding.UTF8, "application/json"));
            return response.Result.Content.ReadAsStringAsync().Result;
        }

        private string GetAuthUrl(string hostUrl, string apiKey, string apiSecret, string protocol)
        {
            Uri url = new Uri(hostUrl);
            string date = DateTime.UtcNow.ToString("R");
            string request_line = $"GET {url.AbsolutePath} HTTP/1.1";
            string signature_origin = $"host: {url.Host}\ndate: {date}\n{request_line}";
            HMAC hmac = HMAC.Create("System.Security.Cryptography.HMACSHA256");
            hmac.Key = Encoding.UTF8.GetBytes(apiSecret);
            var signature_sha = hmac.ComputeHash(Encoding.UTF8.GetBytes(signature_origin));
            string signature = Convert.ToBase64String(signature_sha);
            string authorization_origin = $@"hmac username=""{apiKey}"", algorithm=""hmac-sha256"", headers=""host date request-line"", signature=""{signature}""";
            string authorization = Convert.ToBase64String(Encoding.UTF8.GetBytes(authorization_origin));
            UriBuilder builder = new UriBuilder()
            {
                Scheme = protocol,
                Host = url.Host,
                Path = url.AbsolutePath,
                Query = $"authorization={authorization}&date={date}&host={url.Host}",
            };
            return builder.ToString();
        }

        private static void GetITSAuthUrl(string hostUrl, string apiKey, string apiSecret, string protocol, string body)
        {
            HMAC hmac4Body = HMAC.Create("System.Security.Cryptography.HMACSHA256");
            var digestResult = hmac4Body.ComputeHash(Encoding.UTF8.GetBytes(body));
            var sb = new StringBuilder();
            foreach (var b in digestResult)
            {
                sb.Append(b.ToString("x2"));
            }
            var digest = Convert.ToBase64String(Encoding.UTF8.GetBytes(sb.ToString()));

            Uri url = new Uri(hostUrl);
            var host = url.Host;
            var date = DateTime.UtcNow.ToString("R");
            string request_line = $"POST {url.AbsolutePath} HTTP/1.1";
            string signature_origin = $"host: {host}\ndate: {date}\n{request_line}\ndigest: SHA-256={digest}";
            HMAC hmac = HMAC.Create("System.Security.Cryptography.HMACSHA256");
            hmac.Key = Encoding.UTF8.GetBytes(apiSecret);
            var signature_sha = hmac.ComputeHash(Encoding.UTF8.GetBytes(signature_origin));
            string signature = Convert.ToBase64String(signature_sha);
            var authorization = $@"api_key=""{apiKey}"", algorithm=""hmac-sha256"", headers=""host date request-line digest"", signature=""{signature}""";

            httpClient = new HttpClient
            {
                BaseAddress = new Uri(mtHostUrl),
            };
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authorization);
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Host", host);
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Date", date);
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Digest", $"SHA-256={digest}");
        }

        /// <summary>
        /// Receive video from subscribed participant.
        /// </summary>
        /// <param name="sender">
        /// The sender.
        /// </param>
        /// <param name="e">
        /// The video media received arguments.
        /// </param>
        private void OnVideoMediaReceived(object sender, VideoMediaReceivedEventArgs e)
        {
            this.GraphLogger.Info($"[{e.SocketId}]: Received Video: [VideoMediaReceivedEventArgs(Data=<{e.Buffer.Data.ToString()}>, Length={e.Buffer.Length}, Timestamp={e.Buffer.Timestamp}, Width={e.Buffer.VideoFormat.Width}, Height={e.Buffer.VideoFormat.Height}, ColorFormat={e.Buffer.VideoFormat.VideoColorFormat}, FrameRate={e.Buffer.VideoFormat.FrameRate})]");

            // TBD: Policy Recording bots can record the Video here
            e.Buffer.Dispose();
        }

        /// <summary>
        /// Receive vbss from subscribed participant.
        /// </summary>
        /// <param name="sender">
        /// The sender.
        /// </param>
        /// <param name="e">
        /// The video media received arguments.
        /// </param>
        private void OnVbssMediaReceived(object sender, VideoMediaReceivedEventArgs e)
        {
            this.GraphLogger.Info($"[{e.SocketId}]: Received VBSS: [VideoMediaReceivedEventArgs(Data=<{e.Buffer.Data.ToString()}>, Length={e.Buffer.Length}, Timestamp={e.Buffer.Timestamp}, Width={e.Buffer.VideoFormat.Width}, Height={e.Buffer.VideoFormat.Height}, ColorFormat={e.Buffer.VideoFormat.VideoColorFormat}, FrameRate={e.Buffer.VideoFormat.FrameRate})]");

            // TBD: Policy Recording bots can record the VBSS here
            e.Buffer.Dispose();
        }
    }
}
