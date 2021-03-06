/*
 *  Copyright � 2014 Thomas R. Lawrence
 *    except: "SkeinFish 0.5.0/*.cs", which are Copyright � 2010 Alberto Fajardo
 *    except: "SerpentEngine.cs", which is Copyright � 1997, 1998 Systemics Ltd on behalf of the Cryptix Development Team (but see license discussion at top of that file)
 *    except: "Keccak/*.cs", which are Copyright � 2000 - 2011 The Legion of the Bouncy Castle Inc. (http://www.bouncycastle.org)
 * 
 *  GNU General Public License
 * 
 *  This file is part of Backup (CryptSikyur-Archiver)
 * 
 *  Backup (CryptSikyur-Archiver) is free software: you can redistribute it and/or modify
 *  it under the terms of the GNU General Public License as published by
 *  the Free Software Foundation, either version 3 of the License, or
 *  (at your option) any later version.
 *
 *  This program is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *  GNU General Public License for more details.
 *
 *  You should have received a copy of the GNU General Public License
 *  along with this program.  If not, see <http://www.gnu.org/licenses/>.
 * 
*/
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;

using Diagnostics;
using Exceptions;

namespace Http
{
    ////////////////////////////////////////////////////////////////////////////
    //
    // Http Implementation
    //
    ////////////////////////////////////////////////////////////////////////////

    public static class Constants
    {
        private const int MaxSmallObjectHeapObjectSize = 85000; // http://msdn.microsoft.com/en-us/magazine/cc534993.aspx, http://blogs.msdn.com/b/dotnet/archive/2011/10/04/large-object-heap-improvements-in-net-4-5.aspx
        private const int PageSize = 4096;
        private const int MaxSmallObjectPageDivisibleSize = MaxSmallObjectHeapObjectSize & ~(PageSize - 1);

        public const int BufferSize = MaxSmallObjectPageDivisibleSize;
    }

    public class InsecureConnectionException : MyApplicationException
    {
        public const string DefaultMessage = "The connection to the remote server is insecure. For your safety, the application must terminate the connection and cannot continue.";

        public InsecureConnectionException()
            : base(DefaultMessage)
        {
        }
    }

    public interface ICertificatePinning
    {
        bool RemoteCertificateValidationCallback(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors, TextWriter trace);
    }

    public interface INetworkThrottle
    {
        void WaitBytes(int count);
        int BufferSize { get; }
    }

    public interface IThroughputMeter
    {
        void BytesTransferred(int count);
        long AverageBytesPerSecond { get; }
    }

    public interface IProgressTracker
    {
        long Current { set; }
    }

    public class HttpSettings
    {
        public bool EnableResumableUploads;
        public int? MaxBytesPerWebRequest; // force upload fail & resumable after this many bytes (to exercise the resume code)

        public ICertificatePinning CertificatePinning;

        public int? SendTimeout; // milliseconds; 0 or -1 is infinite
        public int? ReceiveTimeout; // milliseconds; 0 or -1 is infinite

        public IPAddress Socks5Address;
        public int Socks5Port;

        public bool AutoRedirect = true;

        public HttpSettings(bool enableResumableUploads, int? maxBytesPerWebRequest, ICertificatePinning certificatePinning, int? sendTimeout, int? receiveTimeout, bool autoRedirect, IPAddress socks5Address, int socks5Port)
        {
            this.EnableResumableUploads = enableResumableUploads;
            this.MaxBytesPerWebRequest = maxBytesPerWebRequest;
            this.CertificatePinning = certificatePinning;
            this.SendTimeout = sendTimeout;
            this.ReceiveTimeout = receiveTimeout;
            this.AutoRedirect = autoRedirect;
            this.Socks5Address = socks5Address;
            this.Socks5Port = socks5Port;
        }
    }

    public static class HttpGlobalControl
    {
        public static INetworkThrottle NetworkThrottleIn = new NullNetworkThrottle();
        public static INetworkThrottle NetworkThrottleOut = new NullNetworkThrottle();

        public static readonly IThroughputMeter NetworkMeterSent = new BasicThroughputMeter();
        public static readonly IThroughputMeter NetworkMeterReceived = new BasicThroughputMeter();
        public static readonly IThroughputMeter NetworkMeterCombined = new CompositeNetworkMeter(NetworkMeterSent, NetworkMeterReceived);

        public class NullNetworkThrottle : INetworkThrottle
        {
            public void WaitBytes(int count)
            {
            }

            public int BufferSize
            {
                get
                {
                    return Constants.BufferSize;
                }
            }
        }

        public class ActiveNetworkThrottle : INetworkThrottle
        {
            private const int MinApproximateBytesPerSecond = 4096;
            private int approximateBytesPerSecond;

            public ActiveNetworkThrottle(int approximateBytesPerSecond)
            {
                if (approximateBytesPerSecond == 0)
                {
                    throw new ArgumentException();
                }
                this.approximateBytesPerSecond = Math.Max(approximateBytesPerSecond, MinApproximateBytesPerSecond);
            }

            public void WaitBytes(int count)
            {
                // crude, but surprisingly effective
                lock (this)
                {
                    long milliseconds = (1000L * count) / approximateBytesPerSecond;
                    Thread.Sleep((int)milliseconds);
                }
            }

            public int BufferSize
            {
                get
                {
                    const int CyclesPerSecond = 30; // the intent is to avoid interfering with low-latency activity such as VOIP
                    int targetBytesPerCycle = approximateBytesPerSecond / CyclesPerSecond;
                    targetBytesPerCycle /= 4096;
                    targetBytesPerCycle *= 4096;
                    targetBytesPerCycle = Math.Max(4096, Math.Min(Constants.BufferSize, targetBytesPerCycle));
                    return targetBytesPerCycle;
                }
            }
        }

        public static void SetThrottle(int approximateBytesPerSecond)
        {
            NetworkThrottleIn = NetworkThrottleOut = approximateBytesPerSecond == 0
                ? (INetworkThrottle)new NullNetworkThrottle()
                : (INetworkThrottle)new ActiveNetworkThrottle(approximateBytesPerSecond);
        }

        public static void SetThrottleIn(int approximateBytesPerSecond)
        {
            NetworkThrottleIn = approximateBytesPerSecond == 0
                ? (INetworkThrottle)new NullNetworkThrottle()
                : (INetworkThrottle)new ActiveNetworkThrottle(approximateBytesPerSecond);
        }

        public static void SetThrottleOut(int approximateBytesPerSecond)
        {
            NetworkThrottleOut = approximateBytesPerSecond == 0
                ? (INetworkThrottle)new NullNetworkThrottle()
                : (INetworkThrottle)new ActiveNetworkThrottle(approximateBytesPerSecond);
        }

        public static void ThrottleOff()
        {
            SetThrottle(0);
        }

        // formatted as either "<overall:int>" or "<in:int>,<out:int>"
        public static void SetThrottleFromString(string s)
        {
            int i, j;
            string[] p;
            if (Int32.TryParse(s, out i) && (i >= 0))
            {
                Http.HttpGlobalControl.SetThrottle(i);
            }
            else if (((p = s.Split(',')).Length == 2) && Int32.TryParse(p[0], out i) && Int32.TryParse(p[1], out j) && (i >= 0) && (j >= 0))
            {
                Http.HttpGlobalControl.SetThrottleIn(i);
                Http.HttpGlobalControl.SetThrottleOut(j);
            }
            else
            {
                throw new ArgumentException();
            }
        }

        // The meter is used mostly for determining the segment size to use in a multi-stage resumable upload
        // (along with informational display for user). The average should be fairly responsive to overall network
        // conditions, but stability is favored over instantaneous accuracy for periods of a small number of seconds.
        public class BasicThroughputMeter : IThroughputMeter
        {
            public void BytesTransferred(int count)
            {
                lock (this)
                {
                    EnsureCurrent();
                    windows[index] = windows[index] + count;
                }
            }

            public long AverageBytesPerSecond
            {
                get
                {
                    lock (this)
                    {
                        EnsureCurrent();
                        // sum excludes current window (by design); but use current window if it's the only data existing
                        return (width > 1 ? sum / (width - 1) : windows[index]) / SecondsPerWindow;
                    }
                }
            }

            private const int MaxWindows = 5;
            private const int TicksPerSecond = 10000000; // there are 10 million DateTime.Ticks in a second: https://msdn.microsoft.com/en-us/library/system.datetime.ticks%28v=vs.110%29.aspx
            private const int SecondsPerWindow = 3;

            private long[] windows = new long[MaxWindows];
            private int index;
            private int width = 1;
            private long lastEpoch;
            private long sum; // excludes current window

            private void EnsureCurrent() // caller must lock(this)
            {
                long currentEpoch = DateTime.UtcNow.Ticks / (SecondsPerWindow * TicksPerSecond);
                if (currentEpoch - lastEpoch > MaxWindows)
                {
                    lastEpoch = currentEpoch;
                    index = 0;
                    width = 1;
                    sum = 0;
                    Array.Clear(windows, 0, MaxWindows);
                }
                else if (currentEpoch != lastEpoch)
                {
                    for (int i = (int)(currentEpoch - lastEpoch); i > 0; i--)
                    {
                        sum = sum + windows[index]; // enter finished window into running sum
                        width = width + 1 < MaxWindows ? width + 1 : MaxWindows;
                        index = index + 1 < MaxWindows ? index + 1 : 0;
                        sum = sum - windows[index]; // remove just-deleted window from running sum
                        windows[index] = 0;
                    }
                    lastEpoch = currentEpoch;

                    if (sum == 0)
                    {
                        width = 1; // during interruption, shrink history length to improve responsiveness to resumption
                    }
                }
            }
        }

        public class CompositeNetworkMeter : IThroughputMeter
        {
            private IThroughputMeter one, two;

            public CompositeNetworkMeter(IThroughputMeter one, IThroughputMeter two)
            {
                this.one = one;
                this.two = two;
            }

            public void BytesTransferred(int count)
            {
                throw new NotSupportedException();
            }

            public long AverageBytesPerSecond
            {
                get
                {
                    return Math.Max(one.AverageBytesPerSecond, two.AverageBytesPerSecond);
                }
            }
        }
    }

    public static class HttpMethods
    {
        public class CertificatePinningDelegate
        {
            private readonly ICertificatePinning clientPinning;
            private readonly TextWriter trace;

            public CertificatePinningDelegate(ICertificatePinning clientPinning, TextWriter trace)
            {
                this.clientPinning = clientPinning;
                this.trace = trace;
            }

            public bool RemoteCertificateValidationCallback(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
            {
                return clientPinning.RemoteCertificateValidationCallback(sender, certificate, chain, sslPolicyErrors, trace);
            }
        }

        private static WebExceptionStatus Socks5Handshake(Socket socket, string hostName, int hostPort)
        {
            // socks5 greeting
            byte[] proxyGreeting = new byte[]
                {
                    0x05, // socks5
                    1, // count of supported authentication methods
                    0x00, // "no auth" as the one supported method
                };

            socket.Send(proxyGreeting);

            byte[] proxyGreetingResponse = new byte[4096];
            {
                int read = socket.Receive(proxyGreetingResponse);
                Array.Resize(ref proxyGreetingResponse, read);
            }

            if (proxyGreetingResponse[0] != 0x05)
            {
                return WebExceptionStatus.UnknownError;// SocketResultCode.Socks_Proxy_Not_Socks5;
            }
            if (proxyGreetingResponse[1] != 0)
            {
                return WebExceptionStatus.UnknownError;// SocketResultCode.Socks_Proxy_Wont_Support_Auth_Method;
            }

            const bool useTcp = true;
            const IPAddress hostAddress = null; // always have proxy resolve to avoid leaking DNS requests
            List<byte> connect = new List<byte>(new byte[]
            {
                0x05, // socks5
                useTcp ? (byte)0x01 : (byte)0x03, // type (1 = TCP stream, 2 = TCP binding (incoming), 3 = associate UDP port)
                0x00, // reserved
                hostAddress != null ? (byte)0x01 : (byte)0x03, // address type (1 = ipv4 [4 bytes], 3 = domain name [1 len + chars], 4 = ipv6 [16 bytes])
            });
            if (hostAddress != null)
            {
                connect.AddRange(hostAddress.GetAddressBytes());
            }
            else
            {
                if (hostName.Length > Byte.MaxValue)
                {
                    throw new ArgumentOutOfRangeException();
                }
                connect.Add((byte)hostName.Length);
                connect.AddRange(Encoding.ASCII.GetBytes(hostName));
            }
            connect.AddRange(new byte[]
                {
                    (byte)(hostPort >> 8), // destination port hb
                    (byte)hostPort, // destination port lb
                });

            socket.Send(connect.ToArray());

            byte[] connectResponse = new byte[4096];
            {
                int read = socket.Receive(connectResponse);
                Array.Resize(ref connectResponse, read);
            }

            if (connectResponse.Length != 10)
            {
                return WebExceptionStatus.UnknownError;// SocketResultCode.Socks_Proxy_Rejected_Connection_Malformed_Response;
            }
            if (connectResponse[0] != 0x05)
            {
                return WebExceptionStatus.UnknownError;// SocketResultCode.Socks_Proxy_Not_Socks5;
            }
            switch (connectResponse[1])
            {
                default:
                    return WebExceptionStatus.UnknownError;// SocketResultCode.Socks_Proxy_Rejected_Connection_Unknown;
                case 0x00:
                    break;
                case 0x01:
                    return WebExceptionStatus.UnknownError;// SocketResultCode.Socks_Proxy_Rejected_Connection_General_Failure;
                case 0x02:
                    return WebExceptionStatus.UnknownError;// SocketResultCode.Socks_Proxy_Rejected_Connection_Not_Allowed_By_Ruleset;
                case 0x03:
                    return WebExceptionStatus.UnknownError;// SocketResultCode.Socks_Proxy_Rejected_Connection_Network_Unreachable;
                case 0x04:
                    return WebExceptionStatus.UnknownError;// SocketResultCode.Socks_Proxy_Rejected_Connection_Host_Unreachable;
                case 0x05:
                    return WebExceptionStatus.UnknownError;// SocketResultCode.Socks_Proxy_Rejected_Connection_Refused;
                case 0x06:
                    return WebExceptionStatus.UnknownError;// SocketResultCode.Socks_Proxy_Rejected_Connection_TTL_Expired;
                case 0x07:
                    return WebExceptionStatus.UnknownError;// SocketResultCode.Socks_Proxy_Rejected_Connection_Protocol_Error;
                case 0x08:
                    return WebExceptionStatus.UnknownError;// SocketResultCode.Socks_Proxy_Rejected_Connection_Address_Type_Not_Supported;
            }

            return WebExceptionStatus.Success;
        }

        private static string StreamReadLine(Stream stream)
        {
            StringBuilder sb = new StringBuilder();
            byte[] buffer = new byte[1];
            int read;
            while ((read = stream.Read(buffer, 0, 1)) > 0)
            {
                sb.Append((char)buffer[0]);
                if ((sb.Length >= 2) && (sb[sb.Length - 2] == '\r') && (sb[sb.Length - 1] == '\n'))
                {
                    sb.Length = sb.Length - 2; // remove CR-LF
                    break;
                }
            }
            return sb.ToString();
        }

        private static WebExceptionStatus SocketRequest(Uri uriInitial, Uri uri, string verb, IPAddress[] hostAddress, bool twoStageRequest, byte[] requestHeaderBytes, Stream requestBodySource, long requestContentLength, out HttpStatusCode httpStatus, out string[] responseHeaders, Stream responseBodyDestinationNormal, Stream responseBodyDestinationExceptional, IProgressTracker progressTrackerUpload, IProgressTracker progressTrackerDownload, TextWriter trace, IFaultInstance faultInstanceContext, HttpSettings settings)
        {
            byte[] buffer = new byte[Math.Min(HttpGlobalControl.NetworkThrottleIn.BufferSize, HttpGlobalControl.NetworkThrottleOut.BufferSize)];

            bool useTLS = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);

            httpStatus = (HttpStatusCode)0;
            responseHeaders = new string[0];

            try
            {
                IFaultInstance faultInstanceMethod = faultInstanceContext.Select("SocketHttpRequest", String.Format("{0}|{1}", verb, uri));

                bool useSocks5 = settings.Socks5Address != null;
                IPAddress[] connectionAddress = useSocks5 ? new IPAddress[] { settings.Socks5Address } : hostAddress;
                int connectionPort = useSocks5 ? settings.Socks5Port : uri.Port;
                if (useSocks5 && (hostAddress != null))
                {
                    throw new InvalidOperationException("hostAddress must not have been resolved if using socks5 - DNS leak!");
                }

                using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    if (!useSocks5)
                    {
                        socket.Connect(connectionAddress, connectionPort);
                    }
                    else
                    {
                        try
                        {
                            socket.Connect(connectionAddress, connectionPort);
                        }
                        catch (SocketException exception)
                        {
                            return WebExceptionStatus.ProxyNameResolutionFailure;
                        }
                    }
                    if (trace != null)
                    {
                        trace.WriteLine("Connected to {0}", socket.RemoteEndPoint);
                    }

                    if (settings.SendTimeout.HasValue)
                    {
                        socket.SendTimeout = settings.SendTimeout.Value;
                    }
                    if (settings.ReceiveTimeout.HasValue)
                    {
                        socket.ReceiveTimeout = settings.ReceiveTimeout.Value;
                    }

                    if (useSocks5)
                    {
                        if (trace != null)
                        {
                            trace.WriteLine("Socks5 Proxy Handshake {0}:{1}", settings.Socks5Address, settings.Socks5Port);
                        }
                        Socks5Handshake(socket, uri.Authority, uri.Port);
                    }

                    List<string> headers = new List<string>();
                    using (Stream socketStream = !useTLS
                        ? (Stream)new NetworkStream(socket, false/*ownsSocket*/)
                        : (Stream)new SslStream(
                            new NetworkStream(socket, false/*ownsSocket*/),
                            false/*leaveInnerStreamOpen*/,
                            settings.CertificatePinning != null ? new CertificatePinningDelegate(settings.CertificatePinning, trace).RemoteCertificateValidationCallback : (RemoteCertificateValidationCallback)null))
                    {
                        if (useTLS)
                        {
                            SslStream ssl = (SslStream)socketStream;

                            // TODO: When moving out of the stone-age (i.e. to .NET 4.0+), update call
                            // below to use TLS 1.2 or higher only.
                            ssl.AuthenticateAsClient(
                                uri.Host,
                                new X509CertificateCollection()/*no client certs*/,
                                System.Security.Authentication.SslProtocols.Tls/*no ssl2/3*/,
                                true/*checkCertificateRevocation*/);

                            if (trace != null)
                            {
                                trace.WriteLine("SSL/TLS connection properties:");
                                trace.WriteLine("  ssl protocol: {0} ({1})", ssl.SslProtocol, (int)ssl.SslProtocol);
                                trace.WriteLine("  key exchange algorithm: {0} ({1})", ssl.KeyExchangeAlgorithm, (int)ssl.KeyExchangeAlgorithm);
                                trace.WriteLine("  key exchange strength: {0}", ssl.KeyExchangeStrength);
                                trace.WriteLine("  cipher algorithm: {0} ({1})", ssl.CipherAlgorithm, (int)ssl.CipherAlgorithm);
                                trace.WriteLine("  cipher strength: {0}", ssl.CipherStrength);
                                trace.WriteLine("  hash algorithm: {0} ({1})", ssl.HashAlgorithm, (int)ssl.HashAlgorithm);
                                trace.WriteLine("  hash strength: {0}", ssl.HashStrength);
                                trace.WriteLine("  is authenticated: {0}", ssl.IsAuthenticated);
                                trace.WriteLine("  is encrypted: {0}", ssl.IsEncrypted);
                                trace.WriteLine("  is mutually authenticated: {0}", ssl.IsMutuallyAuthenticated);
                                //trace.WriteLine("  is server: {0}", ssl.IsServer);
                                trace.WriteLine("  is signed: {0}", ssl.IsSigned);
                                trace.WriteLine("  remote certificate: {0}", ssl.RemoteCertificate != null ? ssl.RemoteCertificate.ToString(true/*verbose*/).Replace(Environment.NewLine, ";") : null);
                                //trace.WriteLine("  local certificate: {0}", ssl.LocalCertificate != null ? ssl.LocalCertificate.ToString(true/*verbose*/).Replace(Environment.NewLine, ";") : null);
                                trace.WriteLine("  check cert revocation: {0}", ssl.CheckCertRevocationStatus);

                                trace.WriteLine("Connected to {0}", socket.RemoteEndPoint);
                            }

                            if (!ssl.IsAuthenticated || !ssl.IsEncrypted || !ssl.IsSigned || !(ssl.SslProtocol >= System.Security.Authentication.SslProtocols.Tls))
                            {
                                throw new InsecureConnectionException();
                            }

                            if (settings.CertificatePinning == null)
                            {
                                if (trace != null)
                                {
                                    trace.WriteLine("certificate pinning is not available for this connection");
                                }
                            }

                            // If pinning is not enabled, the host name still needs to be verified vs. the certificate's
                            // signed host names. This is a bit of a fussy operation. Since the host name is passed into
                            // SslSteam.AuthenticateAsClient, it is assumed that the host name is validated vs. the
                            // certificate (verified via http://referencesource.microsoft.com).
                            // FUTURE TODO: If a different TLS implementation is used, it should be checked to make sure
                            // the host name is verified.
                        }


                        // write request header

                        socketStream.Write(requestHeaderBytes, 0, requestHeaderBytes.Length);
                        if (HttpGlobalControl.NetworkThrottleOut != null)
                        {
                            HttpGlobalControl.NetworkThrottleOut.WaitBytes(requestHeaderBytes.Length);
                        }
                        if (HttpGlobalControl.NetworkMeterSent != null)
                        {
                            HttpGlobalControl.NetworkMeterSent.BytesTransferred(requestHeaderBytes.Length);
                        }

                        // wait for 100-continue if two-stage request in use

                        if (twoStageRequest)
                        {
                            if (trace != null)
                            {
                                trace.WriteLine("two-stage request - waiting for 100-Continue:");
                            }

                            string line2;
                            List<string> headers2 = new List<string>();
                            while (!String.IsNullOrEmpty(line2 = StreamReadLine(socketStream)))
                            {
                                headers2.Add(line2);
                                if (trace != null)
                                {
                                    trace.WriteLine("  {0}", line2);
                                }
                            }
                            string[] line2Parts;
                            int code = -1;
                            if ((headers2.Count < 1)
                                || String.IsNullOrEmpty(headers2[0])
                                || ((line2Parts = headers2[0].Split(new char[] { ' ' })).Length < 2)
                                || (!line2Parts[0].StartsWith("HTTP"))
                                || !Int32.TryParse(line2Parts[1], out code)
                                || (code != 100))
                            {
                                if (trace != null)
                                {
                                    trace.WriteLine("did not receive 100-Continue, aborting.");
                                }

                                if (code != -1)
                                {
                                    if (trace != null)
                                    {
                                        trace.WriteLine("  server returned status code: {0} ({1})", (int)code, (HttpStatusCode)code);
                                    }

                                    responseHeaders = headers2.ToArray();
                                    return WebExceptionStatus.Success; // caller will handle header
                                }

                                return WebExceptionStatus.ServerProtocolViolation; // unintelligible response
                            }
                        }


                        // write request body

                        IFaultPredicate faultPredicateWriteRequest = faultInstanceMethod.SelectPredicate("RequestBodyBytes");

                        if (requestBodySource != null)
                        {
                            long requestBytesRemaining = requestContentLength;
                            long requestBytesSent = 0;
                            int read;
                            while ((read = requestBodySource.Read(buffer, 0, (int)Math.Min(buffer.Length, requestBytesRemaining))) != 0)
                            {
                                if (HttpGlobalControl.NetworkThrottleOut != null)
                                {
                                    HttpGlobalControl.NetworkThrottleOut.WaitBytes(read);
                                }
                                if (HttpGlobalControl.NetworkMeterSent != null)
                                {
                                    HttpGlobalControl.NetworkMeterSent.BytesTransferred(read);
                                }

                                socketStream.Write(buffer, 0, read);
                                requestBytesSent += read;
                                requestBytesRemaining -= read;

                                if (progressTrackerUpload != null)
                                {
                                    progressTrackerUpload.Current = requestBodySource.Position;
                                }

                                faultPredicateWriteRequest.Test(requestBytesSent); // may throw FaultInjectionException or FaultInjectionPayloadException

                                if (settings.EnableResumableUploads && settings.MaxBytesPerWebRequest.HasValue)
                                {
                                    // If the remote service supports restartable uploads (indicated by the
                                    // subclass constructor setting enableRestartableUploads), then we can do
                                    // the following:
                                    // 1. The upload can be aborted as a matter of course after a decent number
                                    //    of bytes for the purpose of exercising the resume branch of the code.

                                    if (requestBytesSent > settings.MaxBytesPerWebRequest.Value)
                                    {
                                        if (trace != null)
                                        {
                                            trace.WriteLine("Sent {0} bytes this request, more than MaxBytesPerWebRequest ({1}); simulating connection break for resume testing", requestBytesSent, settings.MaxBytesPerWebRequest.Value);
                                        }
                                        return WebExceptionStatus.ReceiveFailure;
                                    }
                                }

                                if (HttpGlobalControl.NetworkThrottleOut != null)
                                {
                                    int currentRecommendedBufferSize = HttpGlobalControl.NetworkThrottleOut.BufferSize;
                                    if (buffer.Length != currentRecommendedBufferSize)
                                    {
                                        Array.Resize(ref buffer, currentRecommendedBufferSize);
                                    }
                                }
                            }
                        }


                        // read response header and body

                        const string ContentLengthHeaderPrefix = "Content-Length:";
                        Stream responseBodyDestination;
                        long? contentLength;
                        int contentLengthIndex = -1;
                        bool chunked = false;
                        {
                            string line;
                            while (!String.IsNullOrEmpty(line = StreamReadLine(socketStream)))
                            {
                                headers.Add(line);
                            }
                            responseHeaders = headers.ToArray();

                            if (headers.Count < 1)
                            {
                                return WebExceptionStatus.ServerProtocolViolation;
                            }

                            string[] parts = headers[0].Split((char)32);
                            if ((parts.Length < 2)
                                || (!parts[0].Equals("HTTP/1.1") && !parts[0].Equals("HTTP/1.0")))
                            {
                                return WebExceptionStatus.ServerProtocolViolation;
                            }
                            httpStatus = (HttpStatusCode)Int32.Parse(parts[1]);

                            if (((verb == "GET") && (httpStatus != (HttpStatusCode)200/*OK*/) && (httpStatus != (HttpStatusCode)206/*PartialContent*/))
                                || ((verb == "DELETE") && (httpStatus != (HttpStatusCode)204/*No Content*/)))
                            {
                                // For GET, if not 200 or 206, then do not modify normal response stream as this
                                // is not data from the requested object but rather error details.
                                // For DELETE, if not 204, then try to use exceptional response stream because
                                // normal stream is usually null since typically no response is expected.
                                responseBodyDestination = responseBodyDestinationExceptional != null ? responseBodyDestinationExceptional : responseBodyDestinationNormal;
                            }
                            else
                            {
                                // For all other verbs, or successful GET, put data in normal response stream.
                                responseBodyDestination = responseBodyDestinationNormal;
                            }

                            chunked = false;
                            const string TransferEncodingHeaderPrefix = "Transfer-Encoding:";
                            int transferEncodingHeaderIndex = Array.FindIndex(responseHeaders, delegate(string candidate) { return candidate.StartsWith(TransferEncodingHeaderPrefix, StringComparison.OrdinalIgnoreCase); });
                            if (transferEncodingHeaderIndex >= 0)
                            {
                                chunked = responseHeaders[transferEncodingHeaderIndex].Substring(TransferEncodingHeaderPrefix.Length).Trim().Equals("chunked", StringComparison.OrdinalIgnoreCase);
                            }

                            if (httpStatus == (HttpStatusCode)204)
                            {
                                contentLength = 0; // "204 No Content" response code - do not try to read
                            }
                            else
                            {
                                contentLengthIndex = Array.FindIndex(responseHeaders, delegate(string candidate) { return candidate.StartsWith(ContentLengthHeaderPrefix, StringComparison.OrdinalIgnoreCase); });
                                if (contentLengthIndex >= 0)
                                {
                                    contentLength = Int64.Parse(responseHeaders[contentLengthIndex].Substring(ContentLengthHeaderPrefix.Length));
                                }
                                else
                                {
                                    contentLength = responseBodyDestination != null ? null : (int?)0;
                                }
                            }
                        }

                        // only needs to be approximate
                        {
                            int approximateResponseHeadersBytes = 0;
                            foreach (string header in responseHeaders)
                            {
                                approximateResponseHeadersBytes += header.Length + Environment.NewLine.Length;
                            }
                            if (HttpGlobalControl.NetworkThrottleIn != null)
                            {
                                HttpGlobalControl.NetworkThrottleIn.WaitBytes(approximateResponseHeadersBytes);
                            }
                            if (HttpGlobalControl.NetworkMeterReceived != null)
                            {
                                HttpGlobalControl.NetworkMeterReceived.BytesTransferred(approximateResponseHeadersBytes);
                            }
                        }

                        IFaultPredicate faultPredicateReadResponse = faultInstanceMethod.SelectPredicate("ResponseBodyBytes");

                        long responseBodyTotalRead = 0;
                        int chunkRemaining = 0;
                        bool chunkedTransferTerminatedNormally = false;
                        Stopwatch lastReceive = new Stopwatch();
                        lastReceive.Start();
                        const int MaxWaitMilliseconds = 5000; // used only for non-chunked transfers that also omitted Content-Length header
                        while (!contentLength.HasValue || (responseBodyTotalRead < contentLength))
                        {
                            if (!contentLength.HasValue && !chunked && (lastReceive.ElapsedMilliseconds > MaxWaitMilliseconds))
                            {
                                break;
                            }

                            long needed = (contentLength.HasValue ? contentLength.Value : Int64.MaxValue) - responseBodyTotalRead;
                            if (chunked)
                            {
                                if (chunkRemaining == 0)
                                {
                                SkipEmbeddedHeader:
                                    if (responseBodyTotalRead > 0)
                                    {
                                        string s = StreamReadLine(socketStream);
                                        if (!String.IsNullOrEmpty(s))
                                        {
                                            return WebExceptionStatus.ServerProtocolViolation;
                                        }
                                    }
                                    string hex = StreamReadLine(socketStream);
                                    if (0 <= hex.IndexOf(':'))
                                    {
                                        goto SkipEmbeddedHeader;
                                    }
                                    hex = hex.Trim();
                                    chunkRemaining = 0;
                                    foreach (char c in hex)
                                    {
                                        int value = "0123456789abcdef".IndexOf(Char.ToLower(c));
                                        if (value < 0)
                                        {
                                            return WebExceptionStatus.ServerProtocolViolation;
                                        }
                                        chunkRemaining = (chunkRemaining << 4) + value;
                                    }
                                    if (chunkRemaining == 0)
                                    {
                                        contentLength = responseBodyTotalRead;
                                        chunkedTransferTerminatedNormally = true;
                                    }
                                }

                                needed = Math.Min(needed, chunkRemaining);
                            }

                            if (HttpGlobalControl.NetworkThrottleIn != null)
                            {
                                int currentRecommendedBufferSize = HttpGlobalControl.NetworkThrottleIn.BufferSize;
                                if (buffer.Length != currentRecommendedBufferSize)
                                {
                                    Array.Resize(ref buffer, currentRecommendedBufferSize);
                                }
                            }
                            needed = Math.Min(buffer.Length, needed);
                            Debug.Assert(needed >= 0);
                            int read = socketStream.Read(buffer, 0, (int)needed);
                            if (read != 0)
                            {
                                lastReceive.Reset();
                                lastReceive.Start();
                            }
                            else
                            {
                                if (lastReceive.ElapsedMilliseconds > socketStream.ReadTimeout)
                                {
                                    return WebExceptionStatus.Timeout;
                                }
                                Thread.Sleep(100);
                            }
                            if (HttpGlobalControl.NetworkThrottleIn != null)
                            {
                                HttpGlobalControl.NetworkThrottleIn.WaitBytes(read);
                            }
                            if (HttpGlobalControl.NetworkMeterReceived != null)
                            {
                                HttpGlobalControl.NetworkMeterReceived.BytesTransferred(read);
                            }
                            responseBodyDestination.Write(buffer, 0, read);
                            chunkRemaining -= read;
                            responseBodyTotalRead += read;

                            if (progressTrackerDownload != null)
                            {
                                progressTrackerDownload.Current = responseBodyDestination.Position;
                            }

                            faultPredicateReadResponse.Test(responseBodyTotalRead); // may throw FaultInjectionException or FaultInjectionPayloadException
                        }

                        if (chunked && chunkedTransferTerminatedNormally)
                        {
                            // synthesize a Content-Length header from chunked transfer
                            if (contentLengthIndex < 0)
                            {
                                contentLengthIndex = responseHeaders.Length;
                                Array.Resize(ref responseHeaders, responseHeaders.Length + 1);
                            }
                            responseHeaders[contentLengthIndex] = String.Format("{0} {1}", ContentLengthHeaderPrefix, contentLength);
                        }
                    }
                }
            }
            catch (FaultTemplateNode.FaultInjectionPayloadException exception)
            {
                if (trace != null)
                {
                    trace.WriteLine("FaultInjectionPayloadException: {0} [{1}] " + Environment.NewLine + "{2}", exception.Message, exception.Payload, exception.StackTrace);
                }
                const string webPrefix = "web=";
                const string statusPrefix = "status=";
                if (exception.Payload.StartsWith(webPrefix))
                {
                    return (WebExceptionStatus)Int32.Parse(exception.Payload.Substring(webPrefix.Length));
                }
                else if (exception.Payload.StartsWith(statusPrefix))
                {
                    httpStatus = (HttpStatusCode)Int32.Parse(exception.Payload.Substring(statusPrefix.Length));
                    if ((responseHeaders != null) && (responseHeaders.Length > 0))
                    {
                        responseHeaders[0] = String.Format("{0} {1}", responseHeaders[0].Substring(0, responseHeaders[0].IndexOf(' ')), (int)httpStatus);
                    }
                    return WebExceptionStatus.ProtocolError;
                }
                else
                {
                    throw new InvalidOperationException("Invalid fault injection payload");
                }
            }
            catch (InsecureConnectionException)
            {
                throw;
            }
            catch (Exception exception) // expect IOException, SocketException, at least...
            {
                if (trace != null)
                {
                    trace.WriteLine("Exception: {0}", exception);
                }
                return WebExceptionStatus.ReceiveFailure;
            }

            return WebExceptionStatus.Success;
        }

        private static readonly string[] SecuritySensitiveHeaders = new string[] { "Authorization" };
        private static void WriteHeader(string key, string value, TextWriter headersWriter, TextWriter trace)
        {
            headersWriter.WriteLine("{0}: {1}", key, value);

            if (trace != null)
            {
                string traceValue1 = null;
                string traceValue2 = value;
                if (Array.IndexOf(SecuritySensitiveHeaders, key) >= 0)
                {
                    traceValue2 = !String.IsNullOrEmpty(traceValue2) ? traceValue2 : String.Empty;
                    const string BearerPrefix = "Bearer ";
                    if (traceValue2.StartsWith(BearerPrefix))
                    {
                        traceValue1 = traceValue2.Substring(0, BearerPrefix.Length);
                        traceValue2 = traceValue2.Substring(BearerPrefix.Length);
                    }
                    traceValue2 = LogWriter.ScrubSecuritySensitiveValue(traceValue2.Trim());
                }
                trace.WriteLine("  {0}: {1}{2}", key, traceValue1, traceValue2);
            }
        }

        private static readonly string[] ForbiddenHeaders = new string[] { "Accept-Encoding", /*"Content-Length",*/ "Expect", "Connection" };

        public static bool IsHeaderForbidden(string header)
        {
            return Array.FindIndex(ForbiddenHeaders, delegate(string candidate) { return String.Equals(candidate, header, StringComparison.OrdinalIgnoreCase); }) >= 0;
        }

        public static WebExceptionStatus SocketHttpRequest(Uri uriInitial, IPAddress[] hostAddress, string verb, KeyValuePair<string, string>[] requestHeaders, Stream requestBodySource, out HttpStatusCode httpStatus, out KeyValuePair<string, string>[] responseHeaders, Stream responseBodyDestination, out string finalUrl, IProgressTracker progressTrackerUpload, IProgressTracker progressTrackerDownload, TextWriter trace, IFaultInstance faultInstanceContext, HttpSettings settings, bool? autoRedirect)
        {
            Uri uri = uriInitial;

            if (trace != null)
            {
                trace.WriteLine("+SocketHttpRequest(url={0}, hostAddress={1}, verb={2}, request-body={3}, response-body={4})", uri, hostAddress, verb, LogWriter.ToString(requestBodySource), LogWriter.ToString(responseBodyDestination, true/*omitContent*/));
            }

            foreach (string forbiddenHeader in ForbiddenHeaders)
            {
                if (Array.FindIndex(requestHeaders, delegate(KeyValuePair<string, string> candidate) { return String.Equals(candidate.Key, forbiddenHeader, StringComparison.OrdinalIgnoreCase); }) >= 0)
                {
                    throw new ArgumentException();
                }
            }

            int redirectCount = 0;
            const int MaxRedirects = 15;
            finalUrl = null;

            long requestContentLength;
            {
                int contentLengthRequestHeaderIndex = Array.FindIndex(requestHeaders, delegate(KeyValuePair<string, string> candidate) { return String.Equals(candidate.Key, "Content-Length", StringComparison.OrdinalIgnoreCase); });
                if (contentLengthRequestHeaderIndex >= 0)
                {
                    requestContentLength = Int64.Parse(requestHeaders[contentLengthRequestHeaderIndex].Value);
                }
                else
                {
                    requestContentLength = requestBodySource != null ? requestBodySource.Length - requestBodySource.Position : 0;
                }
            }

            // Use "Expect: 100-continue" method if larger than this - gives remote server a chance
            // to reject request if Content-Length is exceeds service's max file size.
            const int MaxOneStagePutBodyLength = 20 * 1024 * 1024; // ensure larger than Microsoft OneDrive upload chunk size - Microsoft doesn't work well with this option
            bool twoStageRequest = String.Equals(verb, "PUT") && (requestContentLength > MaxOneStagePutBodyLength);

        Restart:
            if (!uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                && !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException();
            }

            httpStatus = (HttpStatusCode)0;
            responseHeaders = new KeyValuePair<string, string>[0];

            byte[] requestHeaderBytes;
            using (MemoryStream stream = new MemoryStream())
            {
                using (TextWriter writer = new StreamWriter(stream))
                {
                    string firstLine = String.Format("{0} {1} HTTP/1.1", verb, uri.PathAndQuery);
                    writer.WriteLine(firstLine);
                    if (trace != null)
                    {
                        trace.WriteLine("Request headers:");
                        trace.WriteLine("  {0}", firstLine);
                    }

                    WriteHeader("Host", uri.Host, writer, trace);
                    foreach (KeyValuePair<string, string> header in requestHeaders)
                    {
                        WriteHeader(header.Key, header.Value, writer, trace);
                    }
                    WriteHeader("Accept-Encoding", "gzip, deflate", writer, trace);
                    // If caller hasn't provided Content-Length, generate it here using basic assumptions
                    if (Array.FindIndex(requestHeaders, delegate(KeyValuePair<string, string> candidate) { return String.Equals(candidate.Key, "Content-Length", StringComparison.OrdinalIgnoreCase); }) < 0)
                    {
                        // Is there any harm in always writing Content-Length header?
                        WriteHeader("Content-Length", ((requestBodySource != null) && (requestBodySource.Length > requestBodySource.Position) ? requestBodySource.Length - requestBodySource.Position : 0).ToString(), writer, trace);
                    }
                    if (twoStageRequest)
                    {
                        WriteHeader("Expect", "100-continue", writer, trace);
                    }
                    WriteHeader("Connection", "keep-alive", writer, trace); // HTTP 1.0 superstition
                    writer.WriteLine();
                }
                requestHeaderBytes = stream.ToArray();
            }


            WebExceptionStatus result;
            string[] responseHeadersLines;
            long responseBodyDestinationStart, responseBodyDestinationEnd, responseBodyBytesReceived;
            using (MemoryStream responseBodyDestinationExceptional = new MemoryStream())
            {
                responseBodyDestinationStart = (responseBodyDestination != null) ? responseBodyDestination.Position : 0;

                result = SocketRequest(
                    uriInitial,
                    uri,
                    verb,
                    hostAddress,
                    twoStageRequest,
                    requestHeaderBytes,
                    requestBodySource,
                    requestContentLength,
                    out httpStatus,
                    out responseHeadersLines,
                    responseBodyDestination,
                    responseBodyDestinationExceptional,
                    progressTrackerUpload,
                    progressTrackerDownload,
                    trace,
                    faultInstanceContext,
                    settings);

                responseBodyDestinationEnd = (responseBodyDestination != null) ? responseBodyDestination.Position : 0;
                responseBodyBytesReceived = responseBodyDestinationEnd - responseBodyDestinationStart;

                if (trace != null)
                {
                    trace.WriteLine("Socket request result: {0} ({1})", (int)result, result);
                    trace.WriteLine("Response headers:");
                    foreach (string s in responseHeadersLines)
                    {
                        trace.WriteLine("  {0}", s);
                    }
                }

                List<KeyValuePair<string, string>> responseHeadersList = new List<KeyValuePair<string, string>>(responseHeadersLines.Length);
                for (int i = 1; i < responseHeadersLines.Length; i++)
                {
                    int marker = responseHeadersLines[i].IndexOf(':');
                    if (marker < 0)
                    {
                        throw new InvalidDataException();
                    }
                    string key = responseHeadersLines[i].Substring(0, marker);
                    string value = responseHeadersLines[i].Substring(marker + 1).Trim();
                    responseHeadersList.Add(new KeyValuePair<string, string>(key, value));
                }
                responseHeaders = responseHeadersList.ToArray();

                if (responseBodyDestinationExceptional.Length != 0)
                {
                    DecompressStreamInPlace(responseBodyDestinationExceptional, responseHeaders, true/*updateHeaders*/);
                    if (trace != null)
                    {
                        trace.WriteLine("unsuccessful GET (not 200 and not 206) response body: {0}", LogWriter.ToString(responseBodyDestinationExceptional));
                    }
                }
            }

            int contentLengthHeaderIndex = Array.FindIndex(responseHeaders, delegate(KeyValuePair<string, string> candidate) { return String.Equals(candidate.Key, "Content-Length", StringComparison.OrdinalIgnoreCase); });
            if (contentLengthHeaderIndex >= 0)
            {
                long contentLengthExpected;
                if (!Int64.TryParse(responseHeaders[contentLengthHeaderIndex].Value, out contentLengthExpected))
                {
                    return WebExceptionStatus.ServerProtocolViolation;
                }
                if ((contentLengthExpected != responseBodyBytesReceived) && ((httpStatus >= (HttpStatusCode)200) && (httpStatus <= (HttpStatusCode)299)))
                {
                    // for various types of non-success responses, permit declared body to be missing because some servers don't send it
                    if (result == WebExceptionStatus.Success)
                    {
                        result = WebExceptionStatus.ReceiveFailure;
                    }
                }
            }

            if (responseBodyDestination != null)
            {
                DecompressStreamInPlace(responseBodyDestination, ref responseBodyDestinationStart, ref responseBodyDestinationEnd, ref responseBodyBytesReceived, responseHeaders, true/*updateHeaders*/);
            }

            if ((autoRedirect.HasValue ? autoRedirect.Value : settings.AutoRedirect) && ((httpStatus >= (HttpStatusCode)300) && (httpStatus <= (HttpStatusCode)307)))
            {
                int locationHeaderIndex = Array.FindIndex(responseHeaders, delegate(KeyValuePair<string, string> candidate) { return String.Equals(candidate.Key, "Location", StringComparison.OrdinalIgnoreCase); });
                if (locationHeaderIndex >= 0)
                {
                    if (trace != null)
                    {
                        if (Array.FindAll(responseHeaders, delegate(KeyValuePair<string, string> candidate) { return String.Equals(candidate.Key, "Location", StringComparison.OrdinalIgnoreCase); }).Length > 1)
                        {
                            trace.WriteLine(" NOTICE: multiple Location response headers present - using first one (http status was {0} {1})", (int)httpStatus, httpStatus);
                        }
                    }

                    redirectCount++;
                    if (redirectCount > MaxRedirects)
                    {
                        result = WebExceptionStatus.UnknownError;
                        goto Exit;
                    }
                    string location = responseHeaders[locationHeaderIndex].Value;
                    if (location.StartsWith("/"))
                    {
                        uri = new Uri(uri, location);
                    }
                    else
                    {
                        uri = new Uri(location);
                    }

                    if (trace != null)
                    {
                        trace.WriteLine("auto-redirecting to {0}", uri);
                    }

                    goto Restart;
                }
            }


            finalUrl = uri.ToString();

        Exit:
            if (trace != null)
            {
                trace.WriteLine("-SocketHttpRequest returns {0} ({1})", (int)result, result);
            }
            return result;
        }

        private static void DecompressStreamInPlace(Stream responseBodyDestination, ref long responseBodyDestinationStart, ref long responseBodyDestinationEnd, ref long responseBodyBytesReceived, KeyValuePair<string, string>[] responseHeaders, bool updateHeaders)
        {
            int contentEncodingHeaderIndex = Array.FindIndex(responseHeaders, delegate(KeyValuePair<string, string> candidate) { return String.Equals(candidate.Key, "Content-Encoding", StringComparison.OrdinalIgnoreCase); });
            if (contentEncodingHeaderIndex >= 0)
            {
                bool gzip = responseHeaders[contentEncodingHeaderIndex].Value.Equals("gzip", StringComparison.OrdinalIgnoreCase);
                bool deflate = responseHeaders[contentEncodingHeaderIndex].Value.Equals("deflate", StringComparison.OrdinalIgnoreCase);
                if (!gzip && !deflate)
                {
                    throw new NotSupportedException(String.Format("Content-Encoding: {0}", responseHeaders[contentEncodingHeaderIndex].Value));
                }

                byte[] buffer = new byte[Constants.BufferSize];

                string tempPath = Path.GetTempFileName();
                using (Stream tempStream = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read))
                {
                    responseBodyDestination.Position = responseBodyDestinationStart;

                    while (responseBodyDestinationEnd > responseBodyDestination.Position)
                    {
                        int needed = (int)Math.Min(buffer.Length, responseBodyDestinationEnd - responseBodyDestination.Position);
                        int read;
                        read = responseBodyDestination.Read(buffer, 0, needed);
                        tempStream.Write(buffer, 0, read);
                    }

                    tempStream.Position = 0;

                    responseBodyDestination.Position = responseBodyDestinationStart;
                    responseBodyDestination.SetLength(responseBodyDestinationStart);

                    using (Stream inputStream = gzip ? (Stream)new GZipStream(tempStream, CompressionMode.Decompress) : (Stream)new DeflateStream(tempStream, CompressionMode.Decompress))
                    {
                        int read;
                        while ((read = inputStream.Read(buffer, 0, buffer.Length)) != 0)
                        {
                            responseBodyDestination.Write(buffer, 0, read);
                        }
                    }

                    responseBodyDestinationEnd = responseBodyDestination.Position;
                    responseBodyBytesReceived = responseBodyDestinationEnd - responseBodyDestinationStart;

                    if (updateHeaders)
                    {
                        int contentLengthHeaderIndex = Array.FindIndex(responseHeaders, delegate(KeyValuePair<string, string> candidate) { return String.Equals(candidate.Key, "Content-Length", StringComparison.OrdinalIgnoreCase); });
                        if (contentLengthHeaderIndex >= 0)
                        {
                            responseHeaders[contentLengthHeaderIndex] = new KeyValuePair<string, string>("Content-Length", responseBodyBytesReceived.ToString());
                        }
                    }
                }
                File.Delete(tempPath);

                if (updateHeaders)
                {
                    responseHeaders[contentEncodingHeaderIndex] = new KeyValuePair<string, string>();
                }
            }
        }

        private static void DecompressStreamInPlace(Stream responseBodyDestination, KeyValuePair<string, string>[] responseHeaders, bool updateHeaders)
        {
            long responseBodyDestinationStart, responseBodyDestinationEnd, responseBodyBytesReceived;
            responseBodyDestinationStart = 0;
            responseBodyDestinationEnd = responseBodyBytesReceived = responseBodyDestination.Length;
            DecompressStreamInPlace(responseBodyDestination, ref responseBodyDestinationStart, ref responseBodyDestinationEnd, ref responseBodyBytesReceived, responseHeaders, updateHeaders);
        }

        public static WebExceptionStatus DNSLookupName(string hostName, out IPAddress[] hostAddress, TextWriter trace, IFaultInstance faultInstanceContext)
        {
            hostAddress = null;
            try
            {
                IPHostEntry hostInfo = Dns.GetHostEntry(hostName);
                if (hostInfo.AddressList.Length < 1)
                {
                    return WebExceptionStatus.NameResolutionFailure;
                }
                hostAddress = hostInfo.AddressList;
                return WebExceptionStatus.Success;
            }
            catch (Exception exception)
            {
                if (trace != null)
                {
                    trace.WriteLine("DNSLookupName caught exception: {0}", exception);
                }
                return WebExceptionStatus.NameResolutionFailure;
            }
        }
    }
}
