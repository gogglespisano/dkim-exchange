using ConfigurationSettings;
using Configuration.DkimSigner.Exchange;
using Configuration.DkimSigner.GitHub;
using Heijden.DNS;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Configuration.DkimSigner
{
    public partial class MainWindow : Form
    {
        // ##########################################################
        // ##################### Variables ##########################
        // ##########################################################

        private delegate DialogResult ShowMessageBoxCallback(string title, string message, MessageBoxButtons buttons, MessageBoxIcon icon);

        private Settings oConfig = null;
        private Version dkimSignerInstalled = null;
        private Release dkimSignerAvailable = null;
        private TransportService transportService = null;
        private bool bDataUpdated = false;

        // ##########################################################
        // ##################### Construtor #########################
        // ##########################################################

        public MainWindow()
        {
            this.InitializeComponent();

            this.cbLogLevel.SelectedItem = "Information";
            this.cbKeyLength.SelectedItem = UserPreferences.Default.KeyLength.ToString();

            string version = Version.Parse(FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).ProductVersion).ToString().Substring(0, 5);
            this.txtAbout.Text = "Version " + version + "\r\n\r\n" +
                                    Constants.DKIM_SIGNER_NOTICE + "\r\n\r\n" +
                                    Constants.DKIM_SIGNER_LICENCE + "\r\n\r\n" +
                                    Constants.DKIM_SIGNER_AUTHOR + "\r\n\r\n" +
                                    Constants.DKIM_SIGNER_WEBSITE;
        }

        // ##########################################################
        // ####################### Events ###########################
        // ##########################################################

        /// <summary>
        /// Load information in the Windowform
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void MainWindow_Load(object sender, EventArgs e)
        {
            this.CheckExchangeInstalled();
            this.CheckDkimSignerAvailable();
            this.CheckDkimSignerInstalled();

            // Check transport service status each second
            try
            {
                this.transportService = new TransportService();
                this.transportService.StatusChanged += new EventHandler(this.transportService_StatusUptated);
            }
            catch (ExchangeServerException) { }

            // Load setting from XML file
            this.LoadDkimSignerConfig();
        }

        /// <summary>
        /// Confirm the configuration saving before quit the application
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void MainWindow_FormClosing(object sender, FormClosingEventArgs e)
        {
            // Check if the config have been change and haven't been save
            if (!this.CheckSaveConfig())
            {
                e.Cancel = true;
            }
            else
            {
                this.Hide();
                this.transportService.Dispose();
                this.transportService = null;
            }
        }

        private void transportService_StatusUptated(object sender, EventArgs e)
        {
            string sStatus = this.transportService.GetStatus();
            this.txtExchangeStatus.BeginInvoke(new Action(() => this.txtExchangeStatus.Text = (sStatus != null ? sStatus : "Unknown")));
        }

        private void txtExchangeStatus_TextChanged(object sender, EventArgs e)
        {
            bool IsRunning = this.txtExchangeStatus.Text == "Running";
            bool IsStopped = this.txtExchangeStatus.Text == "Stopped";

            this.btStartTransportService.Enabled = IsStopped;
            this.btStopTransportService.Enabled = IsRunning;
            this.btRestartTransportService.Enabled = IsRunning;
        }

        private void cbxPrereleases_CheckedChanged(object sender, EventArgs e)
        {
            this.txtDkimSignerAvailable.Text = "Loading ...";
            this.CheckDkimSignerAvailable();
        }

        private void generic_ValueChanged(object sender, System.EventArgs e)
        {
            this.bDataUpdated = true;
        }

        private void lbxHeadersToSign_SelectedIndexChanged(object sender, EventArgs e)
        {
            this.btHeaderDelete.Enabled = (this.lbxHeadersToSign.SelectedItem != null);
        }

        private void lbxDomains_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (this.lbxDomains.SelectedItems.Count == 0)
            {
                this.txtDomainName.Text = "";
                this.txtDomainSelector.Text = "";
                this.txtDomainPrivateKeyFilename.Text = "";
                this.txtDomainDNS.Text = "";
                this.gbxDomainDetails.Enabled = false;
                this.cbKeyLength.Text = UserPreferences.Default.KeyLength.ToString();
            }
            else
            {
                DomainElement oSelected = (DomainElement) this.lbxDomains.SelectedItem;
                this.txtDomainName.Text = oSelected.Domain;
                this.txtDomainSelector.Text = oSelected.Selector;
                this.txtDomainPrivateKeyFilename.Text = oSelected.PrivateKeyFile;

                if (oSelected.CryptoProvider == null)
                {
                    oSelected.InitElement(Constants.DKIM_SIGNER_PATH);
                }
                else
                {
                    this.cbKeyLength.Text = oSelected.CryptoProvider.KeySize.ToString();
                }

                this.UpdateSuggestedDNS();
                this.txtDomainDNS.Text = "";
                this.gbxDomainDetails.Enabled = true;
                this.btDomainDelete.Enabled = true;
                this.btDomainSave.Enabled = false;
                this.bDataUpdated = false;
            }
        }

        private void txtDomainName_TextChanged(object sender, EventArgs e)
        {
            this.epvDomainSelector.SetError(this.txtDomainName, Uri.CheckHostName(this.txtDomainName.Text) != UriHostNameType.Dns ? "Invalid DNS name. Format: 'example.com'" : null);
            this.txtDNSName.Text = this.txtDomainSelector.Text + "._domainkey." + this.txtDomainName.Text + ".";
            this.btDomainSave.Enabled = true;
            this.bDataUpdated = true;
        }

        private void txtDomainSelector_TextChanged(object sender, EventArgs e)
        {
            this.epvDomainSelector.SetError(this.txtDomainSelector, !Regex.IsMatch(this.txtDomainSelector.Text, @"^[a-z0-9_]{1,63}(?:\.[a-z0-9_]{1,63})?$", RegexOptions.None) ? "The selector should only contain characters, numbers and underscores." : null);
            this.txtDNSName.Text = this.txtDomainSelector.Text + "._domainkey." + this.txtDomainName.Text + ".";
            this.btDomainSave.Enabled = true;
            this.bDataUpdated = true;
        }

        // ##########################################################
        // ################# Internal functions #####################
        // ##########################################################

        /// <summary>
        /// Check if a string is in base64
        /// </summary>
        /// <param name="s"></param>
        /// <returns></returns>
        public static bool IsBase64String(string s)
        {
            s = s.Trim();
            return (s.Length % 4 == 0) && Regex.IsMatch(s, @"^[a-zA-Z0-9\+/]*={0,3}$", RegexOptions.None);
        }

        private DialogResult ShowMessageBox(string title, string message, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            DialogResult? result = null;

            if (this.InvokeRequired)
            {
                ShowMessageBoxCallback c = new ShowMessageBoxCallback(this.ShowMessageBox);
                result = this.Invoke(c, new object[] { title, message, buttons, icon }) as DialogResult?;
            }
            else
            {
                result = MessageBox.Show(this, message, title, buttons, icon);
            }

            if (result == null)
            {
                throw new Exception("Unexpected error from MessageBox.");
            }

            return (DialogResult) result;
        }

        /// <summary>
        /// Check the Microsoft Exchange Transport Service Status
        /// </summary>
        private async void CheckExchangeInstalled()
        {
            string version = "Unknown";

            ExchangeServerException ex = null;
            await Task.Run(() => { try { version = ExchangeServer.GetInstalledVersion(); } catch (ExchangeServerException e) { ex = e; } });
            
            if (ex != null)
            {
                this.ShowMessageBox("Exchange Version Error", "Couldn't determine installed Exchange Version: " + ex.Message, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            this.txtExchangeInstalled.Text = version;

            // Uptade Microsft Exchange Transport Service stuatus
            this.btConfigureTransportService.Enabled = (version != null && version != "Not installed");
            if (!this.btConfigureTransportService.Enabled)
            {
                this.txtExchangeStatus.Text = "Unavailable";
            }
        }

        /// <summary>
        /// Thread safe function for the thread DkimSignerInstalled
        /// </summary>
        private async void CheckDkimSignerInstalled()
        {
            Version oDkimSignerInstalled = null;

            // Check if DKIM Agent is in C:\Program Files\Exchange DkimSigner and get version of DLL
            await Task.Run(() => { try { oDkimSignerInstalled = Version.Parse(FileVersionInfo.GetVersionInfo(Path.Combine(Constants.DKIM_SIGNER_PATH, Constants.DKIM_SIGNER_AGENT_DLL)).ProductVersion); } catch (Exception) { } });

            // Check if DKIM agent have been load in Exchange
            if (oDkimSignerInstalled != null)
            {
                bool IsDkimAgentTransportInstalled = false;

                await Task.Run(() => { try { IsDkimAgentTransportInstalled = !ExchangeServer.IsDkimAgentTransportInstalled(); } catch (Exception) { } });

                if (IsDkimAgentTransportInstalled)
                {
                    oDkimSignerInstalled = null;
                }
            }

            this.txtDkimSignerInstalled.Text = (oDkimSignerInstalled != null ? oDkimSignerInstalled.ToString() : "Not installed");
            this.btConfigureTransportService.Enabled = (oDkimSignerInstalled != null);
            this.dkimSignerInstalled = oDkimSignerInstalled;

            this.SetUpgradeButton();
        }

        /// <summary>
        /// Thread safe function for the thread DkimSignerAvailable
        /// </summary>
        private async void CheckDkimSignerAvailable()
        {
            this.cbxPrereleases.Enabled = false;

            List<Release> aoRelease = null;
            StringBuilder changelog = new StringBuilder("Couldn't get current version.\r\nCheck your Internet connection or restart the application.");

            // Check the lastest Release
            Exception ex = null;
            await Task.Run(() => { try { aoRelease = ApiWrapper.GetAllRelease(cbxPrereleases.Checked); } catch (Exception e) { ex = e; } });

            if(ex != null)
            {
                changelog.Append("\r\nError: " + ex.Message);
            }

            this.dkimSignerAvailable = null;

            if (aoRelease != null)
            {
                changelog.Clear();
                
                this.dkimSignerAvailable = aoRelease[0];
                changelog.AppendLine(aoRelease[0].TagName + " (" + aoRelease[0].CreatedAt.Substring(0, 10) + ")\r\n\t" + aoRelease[0].Body.Replace("\r\n", "\r\n\t") + "\r\n");
                
                for(int i = 1; i < aoRelease.Count; i++)
                {
                    if (this.dkimSignerAvailable.Version < aoRelease[i].Version)
                    {
                        this.dkimSignerAvailable = aoRelease[i];
                    }

                    // TAG (DATE)\r\nIndented Text
                    changelog.AppendLine(aoRelease[i].TagName + " (" + aoRelease[i].CreatedAt.Substring(0, 10) + ")\r\n\t" + aoRelease[i].Body.Replace("\r\n", "\r\n\t") + "\r\n");
                }
            }

            this.txtDkimSignerAvailable.Text = this.dkimSignerAvailable != null ? this.dkimSignerAvailable.Version.ToString() : "Unknown";
            this.txtChangelog.Text = changelog.ToString();
            this.SetUpgradeButton();

            this.cbxPrereleases.Enabled = true;
        }

        private void SetUpgradeButton()
        {
            string text = string.Empty;

            bool IsExchangeInstalled = (this.txtExchangeInstalled.Text != "" && this.txtExchangeInstalled.Text != "Unknown" && this.txtExchangeInstalled.Text != "Loading...");

            if (this.dkimSignerInstalled != null && this.dkimSignerAvailable != null)
            {
                this.btUpgrade.Text = (this.dkimSignerInstalled != null ? (this.dkimSignerInstalled < this.dkimSignerAvailable.Version ? "&Upgrade" : "&Reinstall") : "&Install");
            }

            this.btUpgrade.Enabled = this.dkimSignerAvailable != null && IsExchangeInstalled;
        }

        /// <summary>
        /// Asks the user if he wants to save the current config and saves it.
        /// </summary>
        /// <returns>false if the user pressed cancel. true otherwise</returns>
        private bool CheckSaveConfig()
        {
            bool bStatus = true;

            // IF the configuration have changed
            if (this.bDataUpdated)
            {
                DialogResult result = this.ShowMessageBox("Save changes?", "Do you want to save your changes?", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (result == DialogResult.Cancel ||
                   (result == DialogResult.Yes &&
                   !this.SaveDkimSignerConfig() &&
                   this.ShowMessageBox("Discard changes?", "Error saving config. Do you wan to close anyways? This will discard all the changes!", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.No))
                {
                    bStatus = false;
                }
            }

            return bStatus;
        }

        /// <summary>
        /// Load the current configuration for Exchange DkimSigner from the registry
        /// </summary>
        private void LoadDkimSignerConfig()
        {
            this.oConfig = new Settings();
            this.oConfig.InitHeadersToSign();

            if (!this.oConfig.Load(Path.Combine(Constants.DKIM_SIGNER_PATH, "settings.xml")))
            {
                this.ShowMessageBox("Settings error", "Couldn't load the settings file.\n Setting it to default values.", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            //
            // Log level
            //
            switch (this.oConfig.Loglevel)
            {
                case 1:
                    this.cbLogLevel.Text = "Error";
                    break;
                case 2:
                    this.cbLogLevel.Text = "Warning";
                    break;
                case 3:
                    this.cbLogLevel.Text = "Information";
                    break;
                case 4:
                    this.cbLogLevel.Text = "Debug";
                    break;
                default:
                    this.cbLogLevel.Text = "Information";
                    this.ShowMessageBox("Information", "The log level is invalid. Set to default: Information.", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    break;
            }

            //
            // Algorithm and Canonicalization
            //
            this.rbRsaSha1.Checked = (oConfig.SigningAlgorithm == DkimAlgorithmKind.RsaSha1);
            this.rbSimpleHeaderCanonicalization.Checked = (this.oConfig.HeaderCanonicalization == DkimCanonicalizationKind.Simple);
            this.rbRelaxedHeaderCanonicalization.Checked = (this.oConfig.HeaderCanonicalization == DkimCanonicalizationKind.Relaxed);
            this.rbSimpleBodyCanonicalization.Checked = (this.oConfig.BodyCanonicalization == DkimCanonicalizationKind.Simple);
            this.rbRelaxedBodyCanonicalization.Checked = (this.oConfig.BodyCanonicalization == DkimCanonicalizationKind.Relaxed);

            //
            // Headers to sign
            //
            this.lbxHeadersToSign.Items.Clear();
            foreach (string sItem in this.oConfig.HeadersToSign)
            {
                this.lbxHeadersToSign.Items.Add(sItem);
            }

            //
            // Domain
            //
            DomainElement oCurrentDomain = null;
            if (this.lbxDomains.SelectedItem != null)
            {
                oCurrentDomain = (DomainElement) this.lbxDomains.SelectedItem;
            }

            this.lbxDomains.Items.Clear();
            foreach (DomainElement oConfigDomain in this.oConfig.Domains)
            {
                this.lbxDomains.Items.Add(oConfigDomain);
            }

            if (oCurrentDomain != null)
            {
                this.lbxDomains.SelectedItem = oCurrentDomain;
            }

            this.bDataUpdated = false;
        }

        /// <summary>
        /// Save the new configuration into registry for Exchange DkimSigner
        /// </summary>
        private bool SaveDkimSignerConfig()
        {
            this.oConfig.Loglevel = this.cbLogLevel.SelectedIndex + 1;
            this.oConfig.SigningAlgorithm = (this.rbRsaSha1.Checked ? DkimAlgorithmKind.RsaSha1 : DkimAlgorithmKind.RsaSha256);
            this.oConfig.BodyCanonicalization = (this.rbSimpleBodyCanonicalization.Checked ? DkimCanonicalizationKind.Simple : DkimCanonicalizationKind.Relaxed);
            this.oConfig.HeaderCanonicalization = (this.rbSimpleHeaderCanonicalization.Checked ? DkimCanonicalizationKind.Simple : DkimCanonicalizationKind.Relaxed);

            this.oConfig.HeadersToSign.Clear();
            foreach (string sItem in this.lbxHeadersToSign.Items)
            {
                this.oConfig.HeadersToSign.Add(sItem);
            }

            this.oConfig.Save(Path.Combine(Constants.DKIM_SIGNER_PATH, "settings.xml"));
            this.bDataUpdated = false;

            return true;
        }

        private void UpdateSuggestedDNS(string sRsaPublicKeyBase64 = "")
        {
            string sDNSRecord = "";
            if (sRsaPublicKeyBase64 == string.Empty)
            {
                string sPubKeyPath = this.txtDomainPrivateKeyFilename.Text;

                if (!Path.IsPathRooted(sPubKeyPath))
                {
                    sPubKeyPath = Path.Combine(Constants.DKIM_SIGNER_PATH, "keys", sPubKeyPath);
                }
                
                if (File.Exists(Path.ChangeExtension(sPubKeyPath, ".pub")))
                    sPubKeyPath = Path.ChangeExtension(sPubKeyPath, ".pub");
                else
                    sPubKeyPath += ".pub";

                if (File.Exists(sPubKeyPath))
                {
                    string[] asContents = File.ReadAllLines(sPubKeyPath);

                    if (asContents.Length > 2 && asContents[0].Equals("-----BEGIN PUBLIC KEY-----") && IsBase64String(asContents[1]))
                    {
                        sRsaPublicKeyBase64 = asContents[1];
                    }
                    else
                    {
                        sDNSRecord = "No valid RSA pub key:\n" + sPubKeyPath;
                    }
                }
                else
                {
                    sDNSRecord = "No RSA pub key found:\n" + sPubKeyPath;
                }
            }

            if (sRsaPublicKeyBase64 != null && sRsaPublicKeyBase64 != string.Empty)
            {
                sDNSRecord = "v=DKIM1; k=rsa; p=" + sRsaPublicKeyBase64;
            }

            this.txtDNSRecord.Text = sDNSRecord;
        }

        /// <summary>
        /// Set the domain key path for the keys
        /// </summary>
        /// <param name="sPath"></param>
        private void SetDomainKeyPath(string sPath)
        {
            string sKeyDir = Path.Combine(Constants.DKIM_SIGNER_PATH, "keys");

            if (sPath.StartsWith(sKeyDir))
            {
                sPath = sPath.Substring(sKeyDir.Length + 1);
            }
            else if (this.ShowMessageBox("Move key?", "It is strongly recommended to store all the keys in the directory\n" + sKeyDir + "\nDo you want me to move the key into this directory?", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                List<string> asFile = new List<string>();
                asFile.Add(sPath);
                asFile.Add(sPath + ".pub");
                asFile.Add(sPath + ".pem");

                foreach (string sFile in asFile)
                {
                    if (File.Exists(sFile))
                    {
                        string sFilename = Path.GetFileName(sFile);
                        string sNewPath = Path.Combine(sKeyDir, sFilename);

                        try
                        {
                            File.Move(sFile, sNewPath);
                            sPath = sNewPath.Substring(sKeyDir.Length - 4);
                        }
                        catch (IOException ex)
                        {
                            this.ShowMessageBox("Error moving file", "Couldn't move file:\n" + sFile + "\nto\n" + sNewPath + "\n" + ex.Message, MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }
                }
            }

            this.txtDomainPrivateKeyFilename.Text = sPath;
            this.btDomainSave.Enabled = true;
            this.bDataUpdated = true;
        }

        // ###########################################################
        // ###################### Button click #######################
        // ###########################################################

        /// <summary>
        /// Button "start" Microsoft Exchange Transport Service have been click
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void genericTransportService_Click(object sender, EventArgs e)
        {
            switch(((Button)sender).Name)
            {
                case "btStartTransportService":
                    this.transportService.Do(TransportServiceAction.Start);
                    break;
                case "btStopTransportService":
                    this.transportService.Do(TransportServiceAction.Stop);
                    break;
                case "btRestartTransportService":
                    this.transportService.Do(TransportServiceAction.Restart);
                    break;
            }
        }

        private void btUpgrade_Click(object sender, EventArgs e)
        {
            if (this.btUpgrade.Text == "Reinstall" ? MessageBox.Show(this, "Do you really want to " + this.btUpgrade.Text.ToUpper() + " the DKIM Exchange Agent (new Version: " + txtDkimSignerAvailable.Text + ")?\n", "Confirmation", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes : true)
            {
                try
                {
                    Process.Start(Assembly.GetExecutingAssembly().Location, this.btUpgrade.Text.Contains("Install") ? "--install" : "--upgrade " + this.dkimSignerAvailable.ZipballUrl);
                    this.Close();
                }
                catch (Exception ex)
                {
                    this.ShowMessageBox("Updater error", "Couldn't start the process :\n" + ex.Message, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        /// <summary>
        /// Button "configure" Microsoft Exchange Transport Service have been click
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void btConfigureTransportService_Click(object sender, EventArgs e)
        {
            ExchangeTransportServiceWindow oEtsw = new ExchangeTransportServiceWindow();

            oEtsw.ShowDialog();
            oEtsw.Dispose();
        }

        /// <summary>
        /// Button "add header" have been click
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void btHeaderAdd_Click(object sender, EventArgs e)
        {
            HeaderInputWindow oHiw = new HeaderInputWindow();

            if (oHiw.ShowDialog() == DialogResult.OK)
            {
                this.lbxHeadersToSign.Items.Add(oHiw.txtHeader.Text);
                this.lbxHeadersToSign.SelectedItem = oHiw.txtHeader;
                this.bDataUpdated = true;
            }

            oHiw.Dispose();
        }

        /// <summary>
        /// Button "delete header" have been click
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void btHeaderDelete_Click(object sender, EventArgs e)
        {
            if (this.lbxHeadersToSign.SelectedItem != null)
            {
                this.lbxHeadersToSign.Items.Remove(lbxHeadersToSign.SelectedItem);
                this.bDataUpdated = true;
            }
        }

        /// <summary>
        /// Button "Save configuration" have been click
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void btSaveConfiguration_Click(object sender, EventArgs e)
        {
            this.SaveDkimSignerConfig();
        }

        /// <summary>
        /// Button "add domain" have been click
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void btAddDomain_Click(object sender, EventArgs e)
        {
            if (this.bDataUpdated)
            {
                DialogResult result = this.ShowMessageBox("Save changes?", "Do you want to save the current changes?", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

                if ((result == DialogResult.Yes && !SaveDkimSignerConfig()) || result == DialogResult.Cancel)
                {
                    return;
                }
            }

            this.lbxDomains.ClearSelected();
            this.txtDNSRecord.Text = "";
            this.txtDNSName.Text = "";
            this.txtDNSRecord.Text = "";
            this.gbxDomainDetails.Enabled = true;
            this.btDomainDelete.Enabled = false;
            this.bDataUpdated = false;
        }

        /// <summary>
        /// Button "delete domain" have been click
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void btDomainDelete_Click(object sender, EventArgs e)
        {
            if (this.lbxDomains.SelectedItem != null)
            {
                DomainElement oCurrentDomain = (DomainElement)this.lbxDomains.SelectedItem;
                this.oConfig.Domains.Remove(oCurrentDomain);
                this.lbxDomains.Items.Remove(oCurrentDomain);
                this.lbxDomains.SelectedItem = null;
            }

            string keyFile = Path.Combine(Constants.DKIM_SIGNER_PATH, "keys", this.txtDomainPrivateKeyFilename.Text);

            List<string> asFile = new List<string>();
            asFile.Add(keyFile);
            asFile.Add(keyFile + ".pub");
            asFile.Add(keyFile + ".pem");

            foreach (string sFile in asFile)
            {
                if (File.Exists(sFile) && this.ShowMessageBox("Delete key?", "Do you want me to delete the key file?\n" + sFile, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    try
                    {
                        File.Delete(sFile);
                    }
                    catch (IOException ex)
                    {
                        this.ShowMessageBox("Error deleting file", "Couldn't delete file:\n" + sFile + "\n" + ex.Message, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }

            this.SaveDkimSignerConfig();
        }

        /// <summary>
        /// Button "generate key" in domain configuration have been click
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void btDomainKeyGenerate_Click(object sender, EventArgs e)
        {
            UserPreferences.Default.KeyLength = Convert.ToInt32(this.cbKeyLength.Text, 10);
            UserPreferences.Default.Save(); 
            
            using (SaveFileDialog oFileDialog = new SaveFileDialog())
            {
                oFileDialog.DefaultExt = "xml";
                oFileDialog.Filter = "All files|*.*";
                oFileDialog.Title = "Select a location for the new key file";
                oFileDialog.InitialDirectory = Path.Combine(Constants.DKIM_SIGNER_PATH, "keys");

                if (!Directory.Exists(oFileDialog.InitialDirectory))
                {
                    Directory.CreateDirectory(oFileDialog.InitialDirectory);
                }

                if (this.txtDomainName.Text.Length > 0)
                {
                    oFileDialog.FileName = this.txtDomainName.Text + ".xml";
                }

                if (oFileDialog.ShowDialog() == DialogResult.OK)
                {
                    using (RSACryptoServiceProvider oProvider = new RSACryptoServiceProvider(Convert.ToInt32(this.cbKeyLength.Text, 10))) {
                        CSInteropKeys.AsnKeyBuilder.AsnMessage oPublicEncoded = CSInteropKeys.AsnKeyBuilder.PublicKeyToX509(oProvider.ExportParameters(true));
                        CSInteropKeys.AsnKeyBuilder.AsnMessage oPrivateEncoded = CSInteropKeys.AsnKeyBuilder.PrivateKeyToPKCS8(oProvider.ExportParameters(true));

                        File.WriteAllBytes(oFileDialog.FileName, Encoding.ASCII.GetBytes(oProvider.ToXmlString(true)));
                        File.WriteAllText(oFileDialog.FileName + ".pub", "-----BEGIN PUBLIC KEY-----\r\n" + Convert.ToBase64String(oPublicEncoded.GetBytes()) + "\r\n-----END PUBLIC KEY-----");
                        File.WriteAllText(oFileDialog.FileName + ".pem", "-----BEGIN PRIVATE KEY-----\r\n" + Convert.ToBase64String(oPrivateEncoded.GetBytes()) + "\r\n-----END PRIVATE KEY-----");

                        this.UpdateSuggestedDNS(Convert.ToBase64String(oPublicEncoded.GetBytes()));
                        this.SetDomainKeyPath(oFileDialog.FileName);
                    }
                }
            }
        }

        /// <summary>
        /// Button "select key" in domain configuration have been click
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void btDomainKeySelect_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog oFileDialog = new OpenFileDialog())
            {
                oFileDialog.FileName = "key";
                oFileDialog.Filter = "Key files|*.xml;*.pem|All files|*.*";
                oFileDialog.Title = "Select a private key for signing";
                oFileDialog.InitialDirectory = Path.Combine(Constants.DKIM_SIGNER_PATH, "keys");

                if (oFileDialog.ShowDialog() == DialogResult.OK)
                {
                    this.SetDomainKeyPath(oFileDialog.FileName);
                    this.UpdateSuggestedDNS();
                }
            }
        }

        /// <summary>
        /// Button "check DNS" in domain configuration have been click
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void btDomainCheckDNS_Click(object sender, EventArgs e)
        {
            string sFullDomain = this.txtDomainSelector.Text + "._domainkey." + this.txtDomainName.Text;

            try
            {
                Resolver oResolver = new Resolver();
                oResolver.Recursion = true;
                oResolver.UseCache = false;

                // Get the name server for the domain to avoid DNS caching
                Response oResponse = oResolver.Query(sFullDomain, QType.NS, QClass.IN);
                if (oResponse.RecordsRR.GetLength(0) > 0)
                {
                    RR oNsRecord = oResponse.RecordsRR[0];
                    if (oNsRecord.RECORD.RR.RECORD.GetType() == typeof(RecordSOA))
                    {
                        RecordSOA oSoaRecord = (RecordSOA)oNsRecord.RECORD.RR.RECORD;
                        oResolver.DnsServer = oSoaRecord.MNAME;
                    }
                }

                // Get the TXT record for DKIM
                oResponse = oResolver.Query(sFullDomain, QType.TXT, QClass.IN);
                if (oResponse.RecordsTXT.GetLength(0) > 0)
                {
                    RecordTXT oTxtRecord = oResponse.RecordsTXT[0];
                    this.txtDomainDNS.Text = oTxtRecord.TXT.Count > 0 ? string.Join(string.Empty, oTxtRecord.TXT) : "No record found for " + sFullDomain;
                }
                else
                {
                    this.txtDomainDNS.Text = "No record found for " + sFullDomain;
                }
            }
            catch (Exception ex)
            {
                this.ShowMessageBox("Error", "Coldn't get DNS record:\n" + ex.Message, MessageBoxButtons.OK, MessageBoxIcon.Error);
                this.txtDomainDNS.Text = "Error getting record.";
            }
        }

        /// <summary>
        /// Button "save" in domain configuration have been click
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void btDomainSave_Click(object sender, EventArgs e)
        {
            if (this.epvDomainSelector.GetError(txtDomainName) == "" && this.epvDomainSelector.GetError(txtDomainSelector) == "")
            {
                DomainElement oCurrentDomain;
                bool bAddToList = false;

                if (this.lbxDomains.SelectedItem != null)
                {
                    oCurrentDomain = (DomainElement)this.lbxDomains.SelectedItem;
                }
                else
                {
                    oCurrentDomain = new DomainElement();
                    bAddToList = true;
                }

                oCurrentDomain.Domain = this.txtDomainName.Text;
                oCurrentDomain.Selector = this.txtDomainSelector.Text;
                oCurrentDomain.PrivateKeyFile = this.txtDomainPrivateKeyFilename.Text;

                if (bAddToList)
                {
                    this.oConfig.Domains.Add(oCurrentDomain);
                    this.lbxDomains.Items.Add(oCurrentDomain);
                    this.lbxDomains.SelectedItem = oCurrentDomain;
                }

                if (this.SaveDkimSignerConfig())
                {
                    this.btDomainSave.Enabled = false;
                    this.btDomainDelete.Enabled = true;
                }
            }
            else
            {
                this.ShowMessageBox("Config error", "You first need to fix the errors in your domain configuration before saving.", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Button "Refresh" on EventLog TabPage
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void btEventLogRefresh_Click(object sender, EventArgs e)
        {
            this.btEventLogRefresh.Enabled = false;
            
            await Task.Run(() =>
            {
                dgEventLog.Rows.Clear();
                if (EventLog.SourceExists(Constants.DKIM_SIGNER_EVENTLOG_SOURCE))
                {
                    EventLog oLogger = new EventLog();

                    try {
                        oLogger.Log = EventLog.LogNameFromSourceName(Constants.DKIM_SIGNER_EVENTLOG_SOURCE, ".");

                    }
                    catch (Exception ex)
                    {
                        oLogger.Dispose();
                        MessageBox.Show(this, "Couldn't get EventLog source:\n" + ex.Message, "Error getting EventLog", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        this.btEventLogRefresh.Enabled = true;
                        return;
                    }

                    for (int i = oLogger.Entries.Count - 1; i > 0; i--)
                    {
                        EventLogEntry oEntry;
                        try {
                            oEntry = oLogger.Entries[i];
                        }
                        catch (Exception ex)
                        {
                            oLogger.Dispose();
                            MessageBox.Show(this, "Couldn't get EventLog entry:\n" + ex.Message, "Error getting EventLog", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            this.btEventLogRefresh.Enabled = true;
                            return;
                        }

                        if (oEntry.Source != Constants.DKIM_SIGNER_EVENTLOG_SOURCE)
                        {
                            continue;
                        }

                        Image oImg = null;
                        switch (oEntry.EntryType)
                        {
                            case EventLogEntryType.Information:
                                oImg = SystemIcons.Information.ToBitmap();
                                break;
                            case EventLogEntryType.Warning:
                                oImg = SystemIcons.Warning.ToBitmap();
                                break;
                            case EventLogEntryType.Error:
                                oImg = SystemIcons.Error.ToBitmap();
                                break;
                            case EventLogEntryType.FailureAudit:
                                oImg = SystemIcons.Error.ToBitmap();
                                break;
                            case EventLogEntryType.SuccessAudit:
                                oImg = SystemIcons.Question.ToBitmap();
                                break;
                        }

                        this.dgEventLog.BeginInvoke(new Action(() => dgEventLog.Rows.Add(oImg, oEntry.TimeGenerated.ToString("yyyy-MM-ddTHH:mm:ss.fff"), oEntry.Message)));
                    }

                    oLogger.Dispose();
                }
            });

            this.btEventLogRefresh.Enabled = true;
        }
    }
}
