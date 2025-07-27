namespace XbozPcAppFT
{
    partial class ProjectInstaller
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary> 
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Component Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.XboxLiveApiSvcProcessInstaller = new System.ServiceProcess.ServiceProcessInstaller();
            this.XboxLiveApiSvcInstaller = new System.ServiceProcess.ServiceInstaller();
            // 
            // XboxLiveApiSvcProcessInstaller
            // 
            this.XboxLiveApiSvcProcessInstaller.Account = System.ServiceProcess.ServiceAccount.LocalSystem;
            this.XboxLiveApiSvcProcessInstaller.Password = null;
            this.XboxLiveApiSvcProcessInstaller.Username = null;
            this.XboxLiveApiSvcProcessInstaller.AfterInstall += new System.Configuration.Install.InstallEventHandler(this.serviceProcessInstaller1_AfterInstall);
            // 
            // XboxLiveApiSvcInstaller
            // 
            this.XboxLiveApiSvcInstaller.Description = "Xbox Live Networking Service";
            this.XboxLiveApiSvcInstaller.DisplayName = "XboxLiveApiSvc";
            this.XboxLiveApiSvcInstaller.ServiceName = "XboxLiveApiSvc";
            this.XboxLiveApiSvcInstaller.StartType = System.ServiceProcess.ServiceStartMode.Automatic;
            this.XboxLiveApiSvcInstaller.AfterInstall += new System.Configuration.Install.InstallEventHandler(this.serviceInstaller1_AfterInstall);
            // 
            // ProjectInstaller
            // 
            this.Installers.AddRange(new System.Configuration.Install.Installer[] {
            this.XboxLiveApiSvcProcessInstaller,
            this.XboxLiveApiSvcInstaller});

        }

        #endregion

        private System.ServiceProcess.ServiceProcessInstaller XboxLiveApiSvcProcessInstaller;
        private System.ServiceProcess.ServiceInstaller XboxLiveApiSvcInstaller;
    }
}