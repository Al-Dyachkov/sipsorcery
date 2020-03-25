﻿//-----------------------------------------------------------------------------
// Filename: Program.cs
//
// Description: An example SIP server program to accept and initiate calls.
//
// Author(s):
// Aaron Clauson  (aaron@sipsorcery.com)
// 
// History:
// 19 Mar 2020	Aaron Clauson	Created, Dublin, Ireland.
//
// License: 
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.
//-----------------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorcery.Sys;

namespace SIPSorcery
{
    struct SIPRegisterAccount
    {
        public string Username;
        public string Password;
        public string Domain;
        public int Expiry;

        public SIPRegisterAccount(string username, string password, string domain, int expiry)
        {
            Username = username;
            Password = password;
            Domain = domain;
            Expiry = expiry;
        }
    }

    struct SendSilenceJob
    {
        public Timer SendSilenceTimer;
        public SIPUserAgent UserAgent;

        public SendSilenceJob(Timer timer, SIPUserAgent ua)
        {
            SendSilenceTimer = timer;
            UserAgent = ua;
        }
    }

    class Program
    {
        private static string DEFAULT_CALL_DESTINATION = "sip:*61@192.168.11.48";
        private static string DEFAULT_TRANSFER_DESTINATION = "sip:*61@192.168.11.48";
        private static int SIP_LISTEN_PORT = 5060;
        private const int DTMF_EVENT_PAYLOAD_ID = 101;
        private const int SEND_SILENCE_PERIOD_MS = 20;      // Period in milliseconds to send silence packets.
        private static readonly byte PCMA_SILENCE_BYTE_ZERO = 0x55;
        private static readonly byte PCMA_SILENCE_BYTE_ONE = 0xD5;

        private static Microsoft.Extensions.Logging.ILogger Log = SIPSorcery.Sys.Log.Logger;

        /// <summary>
        /// The set of SIP accounts available for registering and/or authenticating calls.
        /// </summary>
        private static readonly List<SIPRegisterAccount> _sipAccounts = new List<SIPRegisterAccount>
        {
            new SIPRegisterAccount( "softphonesample", "password", "sipsorcery.com", 120)
        };

        private static SIPTransport _sipTransport;

        /// <summary>
        /// Keeps track of the current active calls. It includes both received and placed calls.
        /// </summary>
        private static ConcurrentDictionary<string, SIPUserAgent> _calls = new ConcurrentDictionary<string, SIPUserAgent>();

        /// <summary>
        /// Keeps track of the SIP account registrations.
        /// </summary>
        private static ConcurrentDictionary<string, SIPRegistrationUserAgent> _registrations = new ConcurrentDictionary<string, SIPRegistrationUserAgent>();

        private static Timer _sendSilenceTimer = null;

        static async Task Main()
        {
            Console.WriteLine("SIPSorcery SIP Call Server example.");
            Console.WriteLine("Press 'c' to place a call to the default destination.");
            Console.WriteLine("Press 'd' to send a random DTMF tone to the newest call.");
            Console.WriteLine("Press 'h' to hangup the oldest call.");
            Console.WriteLine("Press 'H' to hangup all calls.");
            Console.WriteLine("Press 'l' to list current calls.");
            Console.WriteLine("Press 'r' to list current registrations.");
            Console.WriteLine("Press 't' to transfer the newest call to the default destination.");
            Console.WriteLine("Press 'q' to quit.");

            AddConsoleLogger();

            // Set up a default SIP transport.
            _sipTransport = new SIPTransport();
            _sipTransport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(IPAddress.Any, SIP_LISTEN_PORT)));
            // If it's desired to listen on a single IP address use the equivalent of:
            //_sipTransport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(IPAddress.Parse("192.168.11.50"), SIP_LISTEN_PORT)));
            EnableTraceLogs(_sipTransport);

            _sipTransport.SIPTransportRequestReceived += OnRequest;

            // Uncomment to enable registrations.
            //StartRegistrations(_sipTransport, _sipAccounts);

            CancellationTokenSource exitCts = new CancellationTokenSource();
            await Task.Run(() => OnKeyPress(exitCts.Token));

            Log.LogInformation("Exiting...");

            SIPSorcery.Net.DNSManager.Stop();

            if (_sipTransport != null)
            {
                Log.LogInformation("Shutting down SIP transport...");
                _sipTransport.Shutdown();
            }
        }

        /// <summary>
        /// Process user key presses.
        /// </summary>
        /// <param name="exit">The cancellation token to set if the user requests to quit the application.</param>
        private static async Task OnKeyPress(CancellationToken exit)
        {
            try
            {
                while (!exit.WaitHandle.WaitOne(0))
                {
                    var keyProps = Console.ReadKey();

                    if (keyProps.KeyChar == 'c')
                    {
                        // Place an outgoing call.
                        var ua = new SIPUserAgent(_sipTransport, null);
                        ua.ClientCallTrying += (uac, resp) => Log.LogInformation($"{uac.CallDescriptor.To} Trying: {resp.StatusCode} {resp.ReasonPhrase}.");
                        ua.ClientCallRinging += (uac, resp) => Log.LogInformation($"{uac.CallDescriptor.To} Ringing: {resp.StatusCode} {resp.ReasonPhrase}.");
                        ua.ClientCallFailed += (uac, err) => Log.LogWarning($"{uac.CallDescriptor.To} Failed: {err}");
                        ua.ClientCallAnswered += (uac, resp) => Log.LogInformation($"{uac.CallDescriptor.To} Answered: {resp.StatusCode} {resp.ReasonPhrase}.");
                        ua.OnDtmfTone += (key, duration) => OnDtmfTone(ua, key, duration);
                        ua.OnCallHungup += OnHangup;

                        var rtpSession = CreateRtpSession(ua);
                        var callResult = await ua.Call(DEFAULT_CALL_DESTINATION, null, null, rtpSession);

                        if (callResult)
                        {
                            _calls.TryAdd(ua.Dialogue.CallId, ua);                           
                        }
                    }
                    else if (keyProps.KeyChar == 'd')
                    {
                        if (_calls.Count == 0)
                        {
                            Log.LogWarning("There are no active calls.");
                        }
                        else
                        {
                            var newestCall = _calls.OrderByDescending(x => x.Value.Dialogue.Inserted).First();
                            byte randomDtmf = (byte)Crypto.GetRandomInt(0, 15);
                            Log.LogInformation($"Sending DTMF {randomDtmf} to {newestCall.Key}.");
                            await newestCall.Value.SendDtmf(randomDtmf);
                        }
                    }
                    else if (keyProps.KeyChar == 'h')
                    {
                        if (_calls.Count == 0)
                        {
                            Log.LogWarning("There are no active calls.");
                        }
                        else
                        {
                            var oldestCall = _calls.OrderBy(x => x.Value.Dialogue.Inserted).First();
                            Log.LogInformation($"Hanging up call {oldestCall.Key}.");
                            oldestCall.Value.OnCallHungup -= OnHangup;
                            oldestCall.Value.Hangup();
                            _calls.TryRemove(oldestCall.Key, out _);
                        }
                    }
                    else if (keyProps.KeyChar == 'H')
                    {
                        if (_calls.Count == 0)
                        {
                            Log.LogWarning("There are no active calls.");
                        }
                        else
                        {
                            foreach(var call in _calls)
                            {
                                Log.LogInformation($"Hanging up call {call.Key}.");
                                call.Value.Hangup();
                            }
                            _calls.Clear();
                        }
                    }
                    else if (keyProps.KeyChar == 'l')
                    {
                        if (_calls.Count == 0)
                        {
                            Log.LogInformation("There are no active calls.");
                        }
                        else
                        {
                            Log.LogInformation("Current call list:");
                            foreach (var call in _calls)
                            {
                                Log.LogInformation($"{call.Key}: {call.Value.Dialogue.RemoteTarget}");
                            }
                        }
                    }
                    else if (keyProps.KeyChar == 'r')
                    {
                        if (_registrations.Count == 0)
                        {
                            Log.LogInformation("There are no active registrations.");
                        }
                        else
                        {
                            Log.LogInformation("Current registration list:");
                            foreach (var registration in _registrations)
                            {
                                Log.LogInformation($"{registration.Key}: is registered {registration.Value.IsRegistered}, last attempt at {registration.Value.LastRegisterAttemptAt}");
                            }
                        }
                    }
                    else if (keyProps.KeyChar == 't')
                    {
                        if (_calls.Count == 0)
                        {
                            Log.LogWarning("There are no active calls.");
                        }
                        else
                        {
                            var newestCall = _calls.OrderByDescending(x => x.Value.Dialogue.Inserted).First();
                            Log.LogInformation($"Transferring call {newestCall.Key} to {DEFAULT_TRANSFER_DESTINATION}.");
                            bool transferResult = await newestCall.Value.BlindTransfer(SIPURI.ParseSIPURI(DEFAULT_TRANSFER_DESTINATION), TimeSpan.FromSeconds(3), exit);

                            if(transferResult)
                            {
                                Log.LogInformation($"Transferring succeeded.");

                                // The remote party will often put us on hold after the transfer.
                                await Task.Delay(1000);

                                newestCall.Value.OnCallHungup -= OnHangup;
                                newestCall.Value.Hangup();
                                _calls.TryRemove(newestCall.Key, out _);
                            }
                            else
                            {
                                Log.LogWarning($"Transfer attempt failed.");
                            }
                        }
                    }
                    else if (keyProps.KeyChar == 'q')
                    {
                        // Quit application.
                        Log.LogInformation("Quitting");
                        break;
                    }
                }
            }
            catch (Exception excp)
            {
                Log.LogError($"Exception OnKeyPress. {excp.Message}.");
            }
        }

        /// <summary>
        /// Example of how to create a basic RTP session object and hook up the event handlers.
        /// </summary>
        /// <param name="ua">The suer agent the RTP session is being created for.</param>
        /// <returns>A new RTP session object.</returns>
        private static RtpAudioSession CreateRtpSession(SIPUserAgent ua)
        {
            var rtpAudioSession = new RtpAudioSession(AddressFamily.InterNetwork);

            // Add the required audio capabilities to the RTP session. These will 
            // automatically get used when creating SDP offers/answers.
            var pcma = new SDPMediaFormat(SDPMediaFormatsEnum.PCMA);

            // RTP event support.
            int clockRate = pcma.GetClockRate();
            SDPMediaFormat rtpEventFormat = new SDPMediaFormat(DTMF_EVENT_PAYLOAD_ID);
            rtpEventFormat.SetFormatAttribute($"{RTPSession.TELEPHONE_EVENT_ATTRIBUTE}/{clockRate}");
            rtpEventFormat.SetFormatParameterAttribute("0-16");

            var audioCapabilities = new List<SDPMediaFormat> { pcma, rtpEventFormat };

            MediaStreamTrack audioTrack = new MediaStreamTrack(null, SDPMediaTypesEnum.audio, false, audioCapabilities);
            rtpAudioSession.addTrack(audioTrack);

            // Wire up the event handler for RTP packets received from the remote party.
            rtpAudioSession.OnRtpPacketReceived += (type, rtp) => OnRtpPacketReceived(ua, type, rtp);

            if(_sendSilenceTimer == null)
            {
                _sendSilenceTimer = new Timer(SendSilence, null, 0, SEND_SILENCE_PERIOD_MS);
            }

            return rtpAudioSession;
        }

        private static void SendSilence(object state)
        {
            uint bufferSize = (uint)SEND_SILENCE_PERIOD_MS;

            byte[] sample = new byte[bufferSize / 2];
            int sampleIndex = 0;

            for (int index = 0; index < bufferSize; index += 2)
            {
                sample[sampleIndex] = PCMA_SILENCE_BYTE_ZERO;
                sample[sampleIndex + 1] = PCMA_SILENCE_BYTE_ONE;
            }

            foreach (var ua in _calls.Values)
            {
                ua.MediaSession.SendMedia(SDPMediaTypesEnum.audio, bufferSize, sample);
            }
        }

        /// <summary>
        /// Event handler for receiving RTP packets.
        /// </summary>
        /// <param name="ua">The SIP user agent associated with the RTP session.</param>
        /// <param name="type">The media type of the RTP packet (audio or video).</param>
        /// <param name="rtpPacket">The RTP packet received from the remote party.</param>
        private static void  OnRtpPacketReceived(SIPUserAgent ua, SDPMediaTypesEnum type, RTPPacket rtpPacket)
        {
            // The raw audio data is available in rtpPacket.Payload.
        }

        /// <summary>
        /// Event handler for receiving a DTMF tone.
        /// </summary>
        /// <param name="ua">The user agent that received the DTMF tone.</param>
        /// <param name="key">The DTMF tone.</param>
        /// <param name="duration">The duration in milliseconds of the tone.</param>
        private static void OnDtmfTone(SIPUserAgent ua, byte key, int duration)
        {
            string callID = ua.Dialogue.CallId;
            Log.LogInformation($"Call {callID} received DTMF tone {key}, duration {duration}ms.");
        }

        /// <summary>
        /// Because this is a server user agent the SIP transport must start listening for client user agents.
        /// </summary>
        private static async Task OnRequest(SIPEndPoint localSIPEndPoint, SIPEndPoint remoteEndPoint, SIPRequest sipRequest)
        {
            try
            {
                if (sipRequest.Header.From != null &&
                sipRequest.Header.From.FromTag != null &&
                sipRequest.Header.To != null &&
                sipRequest.Header.To.ToTag != null)
                {
                    // This is an in-dialog request that will be handled directly by a user agent instance.
                }
                else if (sipRequest.Method == SIPMethodsEnum.INVITE)
                {
                    Log.LogInformation($"Incoming call request: {localSIPEndPoint}<-{remoteEndPoint} {sipRequest.URI}.");

                    SIPUserAgent ua = new SIPUserAgent(_sipTransport, null);
                    ua.OnCallHungup += OnHangup;
                    ua.ServerCallCancelled += (uas) => Log.LogDebug("Incoming call cancelled by remote party.");
                    ua.OnDtmfTone += (key, duration) => OnDtmfTone(ua, key, duration);

                    var uas = ua.AcceptCall(sipRequest);
                    var rtpSession = CreateRtpSession(ua);
                    await ua.Answer(uas, rtpSession);

                    if(ua.IsCallActive)
                    {                      
                        _calls.TryAdd(ua.Dialogue.CallId, ua);
                        Timer sendSilenceTimer = new Timer(SendSilence, ua, 0, SEND_SILENCE_PERIOD_MS);
                    }
                }
                else if (sipRequest.Method == SIPMethodsEnum.BYE)
                {
                    SIPResponse byeResponse = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.CallLegTransactionDoesNotExist, null);
                    await _sipTransport.SendResponseAsync(byeResponse);
                }
                else if (sipRequest.Method == SIPMethodsEnum.SUBSCRIBE)
                {
                    SIPResponse notAllowededResponse = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.MethodNotAllowed, null);
                    await _sipTransport.SendResponseAsync(notAllowededResponse);
                }
                else if (sipRequest.Method == SIPMethodsEnum.OPTIONS || sipRequest.Method == SIPMethodsEnum.REGISTER)
                {
                    SIPResponse optionsResponse = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.Ok, null);
                    await _sipTransport.SendResponseAsync(optionsResponse);
                }
            }
            catch (Exception reqExcp)
            {
                Log.LogWarning($"Exception handling {sipRequest.Method}. {reqExcp.Message}");
            }
        }

        /// <summary>
        /// Remove call from the active calls list.
        /// </summary>
        /// <param name="dialogue">The dialogue that was hungup.</param>
        private static void OnHangup(SIPDialogue dialogue)
        {
            // If the dialogue is null it means the hangup was initiated from our end.
            if (dialogue != null)
            {
                string callID = dialogue.CallId;
                Log.LogInformation($"Call hungup by remote party {callID}.");
                if (_calls.ContainsKey(callID))
                {
                    _calls.TryRemove(callID, out _);
                }
            }

            if(_calls.Count() == 0)
            {
                _sendSilenceTimer.Dispose();
            }
        }

        /// <summary>
        /// Starts a registration agent for each of the supplied SIP accounts.
        /// </summary>
        /// <param name="sipTransport">The SIP transport to use for the registrations.</param>
        /// <param name="sipAccounts">The list of SIP accounts to create a registration for.</param>
        private static void StartRegistrations(SIPTransport sipTransport, List<SIPRegisterAccount> sipAccounts)
        {
            foreach(var sipAccount in sipAccounts)
            {
                var regUserAgent = new SIPRegistrationUserAgent(sipTransport, sipAccount.Username, sipAccount.Password, sipAccount.Domain, sipAccount.Expiry);

                // Event handlers for the different stages of the registration.
                regUserAgent.RegistrationFailed += (uri, err) => Log.LogError($"{uri.ToString()}: {err}");
                regUserAgent.RegistrationTemporaryFailure += (uri, msg) => Log.LogWarning($"{uri.ToString()}: {msg}");
                regUserAgent.RegistrationRemoved += (uri) => Log.LogError($"{uri.ToString()} registration failed.");
                regUserAgent.RegistrationSuccessful += (uri) => Log.LogInformation($"{uri.ToString()} registration succeeded.");

                // Start the thread to perform the initial registration and then periodically resend it.
                regUserAgent.Start();

                _registrations.TryAdd($"{sipAccount.Username}@{sipAccount.Domain}", regUserAgent);
            }
        }

        /// <summary>
        ///  Adds a console logger. Can be omitted if internal SIPSorcery debug and warning messages are not required.
        /// </summary>
        private static void AddConsoleLogger()
        {
            var loggerFactory = new Microsoft.Extensions.Logging.LoggerFactory();
            var loggerConfig = new LoggerConfiguration()
                .Enrich.FromLogContext()
                .MinimumLevel.Is(Serilog.Events.LogEventLevel.Debug)
                .WriteTo.Console(theme: AnsiConsoleTheme.Code)
                .CreateLogger();
            loggerFactory.AddSerilog(loggerConfig);
            SIPSorcery.Sys.Log.LoggerFactory = loggerFactory;
        }

        /// <summary>
        /// Enable detailed SIP log messages.
        /// </summary>
        private static void EnableTraceLogs(SIPTransport sipTransport)
        {
            sipTransport.SIPRequestInTraceEvent += (localEP, remoteEP, req) =>
            {
                Log.LogDebug($"Request received: {localEP}<-{remoteEP}");
                Log.LogDebug(req.ToString());
            };

            sipTransport.SIPRequestOutTraceEvent += (localEP, remoteEP, req) =>
            {
                Log.LogDebug($"Request sent: {localEP}->{remoteEP}");
                Log.LogDebug(req.ToString());
            };

            sipTransport.SIPResponseInTraceEvent += (localEP, remoteEP, resp) =>
            {
                Log.LogDebug($"Response received: {localEP}<-{remoteEP}");
                Log.LogDebug(resp.ToString());
            };

            sipTransport.SIPResponseOutTraceEvent += (localEP, remoteEP, resp) =>
            {
                Log.LogDebug($"Response sent: {localEP}->{remoteEP}");
                Log.LogDebug(resp.ToString());
            };

            sipTransport.SIPRequestRetransmitTraceEvent += (tx, req, count) =>
            {
                Log.LogDebug($"Request retransmit {count} for request {req.StatusLine}, initial transmit {DateTime.Now.Subtract(tx.InitialTransmit).TotalSeconds.ToString("0.###")}s ago.");
            };

            sipTransport.SIPResponseRetransmitTraceEvent += (tx, resp, count) =>
            {
                Log.LogDebug($"Response retransmit {count} for response {resp.ShortDescription}, initial transmit {DateTime.Now.Subtract(tx.InitialTransmit).TotalSeconds.ToString("0.###")}s ago.");
            };
        }
    }
}
