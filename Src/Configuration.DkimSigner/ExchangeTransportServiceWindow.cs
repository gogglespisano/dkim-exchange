﻿using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Configuration.DkimSigner.Exchange;

namespace Configuration.DkimSigner
{
    public partial class ExchangeTransportServiceWindow : Form
    {
        // ##########################################################
        // ##################### Variables ##########################
        // ##########################################################

        private List<TransportServiceAgent> installedAgentsList = null;
        private int currentAgentPriority = 0;

        // ##########################################################
        // ##################### Construtor #########################
        // ##########################################################

        public ExchangeTransportServiceWindow()
        {
            this.InitializeComponent();
        }

        // ##########################################################
        // ####################### Events ###########################
        // ##########################################################

        private void ExchangeTransportServiceWindows_Load(object sender, EventArgs e)
        {
            this.RefreshTransportServiceAgents();
        }

        private void dgvTransportServiceAgents_SelectionChanged(object sender, EventArgs e)
        {
            bool dkimAgentSelected = dgvTransportServiceAgents.SelectedRows.Count == 1 && dgvTransportServiceAgents.SelectedRows[0].Cells["dgvcName"].Value.ToString().Equals(Constants.DKIM_SIGNER_AGENT_NAME);
            this.btUninstall.Enabled = dkimAgentSelected;
            this.btDisable.Enabled = dkimAgentSelected;
            this.refreshMoveButtons(dkimAgentSelected);
        }

        // ##########################################################
        // ################# Internal functions #####################
        // ##########################################################

        private void RefreshTransportServiceAgents()
        {
            installedAgentsList = null;

            try
            {
                installedAgentsList = ExchangeServer.GetTransportServiceAgents();
            }
            catch(Exception) { }

            this.dgvTransportServiceAgents.Rows.Clear();

            if (installedAgentsList != null)
            {
                foreach (TransportServiceAgent oAgent in installedAgentsList)
                {
                    this.dgvTransportServiceAgents.Rows.Add(oAgent.Priority, oAgent.Name, oAgent.Enabled);
                    if (oAgent.Name == Constants.DKIM_SIGNER_AGENT_NAME)
                        currentAgentPriority = oAgent.Priority;
                }
            }
            foreach (DataGridViewRow row in this.dgvTransportServiceAgents.Rows)
            {
                row.Selected = row.Cells["dgvcName"].Value.ToString().Equals(Constants.DKIM_SIGNER_AGENT_NAME);
            }
            
            bool IsDkimAgentTransportInstalled = ExchangeServer.IsDkimAgentTransportInstalled();
            bool IsDkimAgentTransportEnabled = IsDkimAgentTransportInstalled && ExchangeServer.IsDkimAgentTransportEnabled();
            this.btDisable.Text = (IsDkimAgentTransportEnabled ? "Disable" : "Enable");
        }


        private void refreshMoveButtons(bool isEnabled)
        {
            if (!isEnabled)
            {
                this.btMoveUp.Enabled = false;
                this.btMoveDown.Enabled = false;
            }
            else
            {
                this.btMoveUp.Enabled = currentAgentPriority > 1;
                this.btMoveDown.Enabled = true;
            }
        }

        // ###########################################################
        // ###################### Button click #######################
        // ###########################################################

        private void btRefresh_Click(object sender, EventArgs e)
        {
            this.RefreshTransportServiceAgents();
        }

        private void btUninstall_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show(this, "Do you really want to UNINSTALL the DKIM Exchange Agent?\n", "Confirmation", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                try
                {
                    ExchangeServer.UninstallDkimTransportAgent();
                    this.RefreshTransportServiceAgents();
                    TransportService ts = new TransportService();
                    try
                    {
                        ts.Do(TransportServiceAction.Restart);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Couldn't restart MSExchangeTransport Service. Please restart it manually. \n" + ex.Message, "Error restarting Service", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    finally
                    {
                        ts.Dispose();

                    }
                    MessageBox.Show(this, "Transport Agent unregistered from Exchange. Please remove the folder manually: '" + Constants.DKIM_SIGNER_PATH + "'\nWARNING: If you remove the folder, keep a backup of your settings and keys!", "Uninstalled", MessageBoxButtons.OK, MessageBoxIcon.Information);

                    /*if (MessageBox.Show(this, "Transport Agent removed from Exchange. Would you like me to remove all the settings for Exchange DKIM Signer?'", "Remove settings?", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == System.Windows.Forms.DialogResult.Yes)
                    {
                        if (File.Exists(Path.Combine(Constants.DKIM_SIGNER_PATH, "settings.xml")))
                            File.Delete(Path.Combine(Constants.DKIM_SIGNER_PATH, "settings.xml"));
                    }*/
                    /*if (MessageBox.Show(this, "Transport Agent removed from Exchange. Would you like me to remove the folder '" + Constants.DKIM_SIGNER_PATH + "' and all its content?\nWARNING: All your settings and keys will be deleted too!", "Remove files?", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == System.Windows.Forms.DialogResult.Yes)
                    {
                        var dir = new DirectoryInfo(Constants.DKIM_SIGNER_PATH);
                        dir.Delete(true);
                    }*/
                }
                catch (ExchangeServerException ex)
                {
                    MessageBox.Show(this, ex.Message, "Uninstall error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void btDisable_Click(object sender, EventArgs e)
        {
            try
            {
                if (this.btDisable.Text == "Disable")
                {
                    ExchangeServer.DisableDkimTransportAgent();
                }
                else
                {
                    ExchangeServer.EnableDkimTransportAgent();
                }

                this.RefreshTransportServiceAgents();
                this.refreshMoveButtons( true );

                TransportService ts = new TransportService();
                try
                {
                    ts.Do(TransportServiceAction.Restart);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Couldn't restart MSExchangeTransport Service. Please restart it manually. \n" + ex.Message, "Error restarting Service", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    ts.Dispose();
                }
            }
            catch (ExchangeServerException ex)
            {
                MessageBox.Show(this, ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void btMoveUp_Click(object sender, EventArgs e)
        {
            try
            {
                ExchangeServer.SetPriorityDkimTransportAgent(currentAgentPriority - 1);
                this.RefreshTransportServiceAgents();
            }
            catch (ExchangeServerException ex)
            {
                MessageBox.Show(this, ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void btMoveDown_Click(object sender, EventArgs e)
        {
            try
            {
                ExchangeServer.SetPriorityDkimTransportAgent(currentAgentPriority + 1);
                this.RefreshTransportServiceAgents();
            }
            catch (ExchangeServerException ex)
            {
                MessageBox.Show(this, ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void btClose_Click(object sender, EventArgs e)
        {
            this.Close();
        }
    }
}
