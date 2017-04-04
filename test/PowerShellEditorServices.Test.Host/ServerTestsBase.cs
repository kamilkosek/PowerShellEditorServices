//
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//

using Microsoft.PowerShell.EditorServices.Protocol.MessageProtocol;
using Microsoft.PowerShell.EditorServices.Utility;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.PowerShell.EditorServices.Test.Host
{
    public class ServerTestsBase
    {
        private Process serviceProcess;
        protected ProtocolEndpoint protocolClient;

        private ConcurrentDictionary<string, AsyncQueue<object>> eventQueuePerType =
            new ConcurrentDictionary<string, AsyncQueue<object>>();

        private ConcurrentDictionary<string, AsyncQueue<object>> requestQueuePerType =
            new ConcurrentDictionary<string, AsyncQueue<object>>();

        protected async Task<Tuple<int, int>> LaunchService(
            string logPath,
            bool waitForDebugger = false)
        {
            string modulePath = Path.GetFullPath(@"..\..\..\..\..\module");
            string scriptPath = Path.Combine(modulePath, "Start-EditorServices.ps1");

            // TODO: Need to determine the right module version programmatically!
            string editorServicesModuleVersion = "0.12.0";

            string scriptArgs =
                string.Format(
                    "\"" + scriptPath + "\" " +
                    "-EditorServicesVersion \"{0}\" " +
                    "-HostName \\\"PowerShell Editor Services Test Host\\\" " +
                    "-HostProfileId \"Test.PowerShellEditorServices\" " +
                    "-HostVersion \"1.0.0\" " +
                    "-BundledModulesPath \\\"" + modulePath + "\\\" " +
                    "-LogLevel \"Verbose\" " +
                    "-LogPath \"" + logPath + "\" ",
                   editorServicesModuleVersion);

            if (waitForDebugger)
            {
                scriptArgs += "-WaitForDebugger ";
            }

            string[] args =
                new string[]
                {
                    "-NoProfile",
                    "-NonInteractive",
                    "-ExecutionPolicy", "Unrestricted",
                    "-Command \"" + scriptArgs + "\""
                };

            this.serviceProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = string.Join(" ", args),
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8
                },
                EnableRaisingEvents = true,
            };

            // Start the process
            this.serviceProcess.Start();

            // Wait for the server to finish initializing
            Task<string> stdoutTask = this.serviceProcess.StandardOutput.ReadLineAsync();
            Task<string> stderrTask = this.serviceProcess.StandardError.ReadLineAsync();
            Task<string> completedRead = await Task.WhenAny<string>(stdoutTask, stderrTask);

            if (completedRead == stdoutTask)
            {
                JObject result = JObject.Parse(completedRead.Result);
                if (result["status"].Value<string>() == "started")
                {
                    return new Tuple<int, int>(
                        result["languageServicePort"].Value<int>(),
                        result["debugServicePort"].Value<int>());
                }

                return null;
            }
            else
            {
                // Must have read an error?  Keep reading from error stream
                string errorString = completedRead.Result;

                while (true)
                {
                    Task<string> errorRead = this.serviceProcess.StandardError.ReadLineAsync();

                    if (!string.IsNullOrEmpty(errorRead.Result))
                    {
                        errorString += errorRead.Result + Environment.NewLine;
                    }
                    else
                    {
                        break;
                    }
                }

                throw new Exception("Could not launch powershell.exe:\r\n\r\n" + errorString);
            }
        }

        protected void KillService()
        {
            try
            {
                this.serviceProcess.Kill();
            }
            catch (InvalidOperationException)
            {
                // This exception gets thrown if the server process has
                // already existed by the time Kill gets called.
            }
        }

        protected Task<TResult> SendRequest<TParams, TResult>(
            RequestType<TParams, TResult> requestType,
            TParams requestParams)
        {
            return
                this.protocolClient.SendRequest(
                    requestType,
                    requestParams);
        }

        protected Task SendEvent<TParams>(EventType<TParams> eventType, TParams eventParams)
        {
            return
                this.protocolClient.SendEvent(
                    eventType,
                    eventParams);
        }

        protected void QueueEventsForType<TParams>(EventType<TParams> eventType)
        {
            var eventQueue =
                this.eventQueuePerType.AddOrUpdate(
                    eventType.MethodName,
                    new AsyncQueue<object>(),
                    (key, queue) => queue);

            this.protocolClient.SetEventHandler(
                eventType,
                (p, ctx) =>
                {
                    return eventQueue.EnqueueAsync(p);
                });
        }

        protected async Task<TParams> WaitForEvent<TParams>(
            EventType<TParams> eventType,
            int timeoutMilliseconds = 5000)
        {
            Task<TParams> eventTask = null;

            // Use the event queue if one has been registered
            AsyncQueue<object> eventQueue = null;
            if (this.eventQueuePerType.TryGetValue(eventType.MethodName, out eventQueue))
            {
                eventTask =
                    eventQueue
                        .DequeueAsync()
                        .ContinueWith<TParams>(
                            task => (TParams)task.Result);
            }
            else
            {
                TaskCompletionSource<TParams> eventTaskSource = new TaskCompletionSource<TParams>();

                this.protocolClient.SetEventHandler(
                    eventType,
                    (p, ctx) =>
                    {
                        if (!eventTaskSource.Task.IsCompleted)
                        {
                            eventTaskSource.SetResult(p);
                        }

                        return Task.FromResult(true);
                    },
                    true);  // Override any existing handler

                eventTask = eventTaskSource.Task;
            }

            await
                Task.WhenAny(
                    eventTask,
                    Task.Delay(timeoutMilliseconds));

            if (!eventTask.IsCompleted)
            {
                throw new TimeoutException(
                    string.Format(
                        "Timed out waiting for '{0}' event!",
                        eventType.MethodName));
            }

            return await eventTask;
        }

        protected async Task<Tuple<TParams, RequestContext<TResponse>>> WaitForRequest<TParams, TResponse>(
            RequestType<TParams, TResponse> requestType,
            int timeoutMilliseconds = 5000)
        {
            Task<Tuple<TParams, RequestContext<TResponse>>> requestTask = null;

            // Use the request queue if one has been registered
            AsyncQueue<object> requestQueue = null;
            if (this.requestQueuePerType.TryGetValue(requestType.MethodName, out requestQueue))
            {
                requestTask =
                    requestQueue
                        .DequeueAsync()
                        .ContinueWith(
                            task => (Tuple<TParams, RequestContext<TResponse>>)task.Result);
            }
            else
            {
                var requestTaskSource =
                    new TaskCompletionSource<Tuple<TParams, RequestContext<TResponse>>>();

                this.protocolClient.SetRequestHandler(
                    requestType,
                    (p, ctx) =>
                    {
                        if (!requestTaskSource.Task.IsCompleted)
                        {
                            requestTaskSource.SetResult(
                                new Tuple<TParams, RequestContext<TResponse>>(p, ctx));
                        }

                        return Task.FromResult(true);
                    });

                requestTask = requestTaskSource.Task;
            }

            await
                Task.WhenAny(
                    requestTask,
                    Task.Delay(timeoutMilliseconds));

            if (!requestTask.IsCompleted)
            {
                throw new TimeoutException(
                    string.Format(
                        "Timed out waiting for '{0}' request!",
                        requestType.MethodName));
            }

            return await requestTask;
        }
    }
}
