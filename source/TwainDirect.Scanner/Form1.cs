﻿///////////////////////////////////////////////////////////////////////////////////////
//
// TwainDirect.Scanner.Form1
//
// This is our main form.  Our goal is to keep it pretty thin, it's sole purpose
// is to act as a presentation layer for when a windowing system is being used,
// so there's no business logic at this level...
//
///////////////////////////////////////////////////////////////////////////////////////
//  Author          Date            Comment
//  M.McLaughlin    29-Nov-2014     Initial Release
///////////////////////////////////////////////////////////////////////////////////////
//  Copyright (C) 2014-2017 Kodak Alaris Inc.
//
//  Permission is hereby granted, free of charge, to any person obtaining a
//  copy of this software and associated documentation files (the "Software"),
//  to deal in the Software without restriction, including without limitation
//  the rights to use, copy, modify, merge, publish, distribute, sublicense,
//  and/or sell copies of the Software, and to permit persons to whom the
//  Software is furnished to do so, subject to the following conditions:
//
//  The above copyright notice and this permission notice shall be included in
//  all copies or substantial portions of the Software.
//
//  THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
//  IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
//  FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL
//  THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
//  LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
//  FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
//  DEALINGS IN THE SOFTWARE.
///////////////////////////////////////////////////////////////////////////////////////

// Helpers...
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using TwainDirect.Support;

namespace TwainDirect.Scanner
{
    public partial class Form1 : Form, IDisposable
    {
        ///////////////////////////////////////////////////////////////////////////////
        // Public Methods...
        ///////////////////////////////////////////////////////////////////////////////
        #region Public Methods...

        /// <summary>
        /// Initialize stuff for our form...
        /// </summary>
        public Form1()
        {
            // Confirm scan...
            bool blConfirmScan = (Config.Get("confirmscan", null) != null);

            // Init our form...
            InitializeComponent();

            // Handle scaling...
            float fScale = (float)Config.Get("scale", 1.0);
            if (fScale <= 1)
            {
                fScale = 1;
            }
            else if (fScale > 2)
            {
                fScale = 2;
            }
            if (fScale != 1)
            {
                this.Font = new Font(this.Font.FontFamily, this.Font.Size * fScale, this.Font.Style);
            }

            // Events...
            this.FormClosing += new FormClosingEventHandler(Form1_FormClosing);

            // Instantiate our scanner object...
            m_scanner = new Scanner
            (
                Display,
                StopNotification,
                blConfirmScan ? ConfirmScan : (TwainLocalScanner.ConfirmScan)null,
                fScale,
                out m_blNoDevices
            );
            if (m_scanner == null)
            {
                Log.Error("Scanner failed...");
                throw new Exception("Scanner failed...");
            }

            // If we don't have any devices, then don't let the user select
            // the start button...
            if (m_blNoDevices)
            {
                SetButtons(ButtonState.NoDevices);
            }
            else
            {
                SetButtons(ButtonState.WaitingForStart);
            }
        }

        #endregion


        ///////////////////////////////////////////////////////////////////////////////
        // Private Methods...
        ///////////////////////////////////////////////////////////////////////////////
        #region Private Methods...

        /// <summary>
        /// Prompt the user prior to scanning...
        /// </summary>
        /// <returns>the button they pressed...</returns>
        private TwainLocalScanner.ButtonPress ConfirmScan(float a_fScale)
        {
            // Let's make sure we do this in the right place...
            if (InvokeRequired)
            {
                m_buttonpress = TwainLocalScanner.ButtonPress.Cancel;
                Invoke(new MethodInvoker(delegate { m_buttonpress = ConfirmScan(a_fScale); }));
                return (m_buttonpress);
            }

            DialogResult dialogresult = DialogResult.No;
            ConfirmScan confirmscan;

            // Ask the question...
            confirmscan = new ConfirmScan(a_fScale);
            dialogresult = confirmscan.ShowDialog(this);
            confirmscan.Dispose();
            confirmscan = null;

            // Okay...
            if (dialogresult == DialogResult.Yes)
            {
                return (TwainLocalScanner.ButtonPress.OK);
            }

            // Nope...
            return (TwainLocalScanner.ButtonPress.Cancel);
        }

        /// <summary>
        /// Set the buttons based on the current state...
        /// </summary>
        private void SetButtons(ButtonState a_ebuttonstate)
        {
            switch (a_ebuttonstate)
            {
                // When we first start up, use this...
                default:
                case ButtonState.Undefined:
                    m_buttonRegister.Enabled = false;
                    m_buttonStart.Enabled = false;
                    m_buttonStop.Enabled = false;
                    break;

                // We have no devices, they need to register...
                case ButtonState.NoDevices:
                    m_buttonRegister.Enabled = true;
                    m_buttonStart.Enabled = false;
                    m_buttonStop.Enabled = false;
                    break;

                // We have devices, they can register or start...
                case ButtonState.WaitingForStart:
                    m_buttonRegister.Enabled = true;
                    m_buttonStart.Enabled = true;
                    m_buttonStop.Enabled = false;
                    break;

                // We're waiting for a command, they can stop...
                case ButtonState.Started:
                    m_buttonRegister.Enabled = false;
                    m_buttonStart.Enabled = false;
                    m_buttonStop.Enabled = true;
                    break;
            }
        }

        /// <summary>
        /// We've been told that monitoring has stopped...
        /// </summary>
        private void StopNotification(bool a_blNoDevices)
        {
            if (a_blNoDevices)
            {
                SetButtons(ButtonState.NoDevices);
            }
            else
            {
                SetButtons(ButtonState.WaitingForStart);
            }
        }

        /// <summary>
        /// Input text...
        /// </summary>
        /// <param name="title">title of the box</param>
        /// <param name="promptText">prompt to the user</param>
        /// <param name="value">text typed by the user</param>
        /// <returns>button pressed</returns>
        private static DialogResult InputBox(string a_szTitle, string a_szPrompt, ref string a_szValue)
        {
            DialogResult dialogResult = DialogResult.Cancel;
            Form form = null;
            Label label = null;
            TextBox textBox = null;
            Button buttonOk = null;
            Button buttonCancel = null;

            try
            {
                form = new Form();
                label = new Label();
                textBox = new TextBox();
                buttonOk = new Button();
                buttonCancel = new Button();

                form.Text = a_szTitle;
                label.Text = a_szPrompt;
                textBox.Text = a_szValue;

                buttonOk.Text = "OK";
                buttonCancel.Text = "Cancel";
                buttonOk.DialogResult = DialogResult.OK;
                buttonCancel.DialogResult = DialogResult.Cancel;

                label.SetBounds(9, 20, 372, 13);
                textBox.SetBounds(12, 36, 372, 20);
                buttonOk.SetBounds(228, 72, 75, 23);
                buttonCancel.SetBounds(309, 72, 75, 23);

                label.AutoSize = true;
                textBox.Anchor = textBox.Anchor | AnchorStyles.Right;
                buttonOk.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
                buttonCancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;

                form.ClientSize = new Size(396, 107);
                form.Controls.AddRange(new Control[] { label, textBox, buttonOk, buttonCancel });
                form.ClientSize = new Size(Math.Max(300, label.Right + 10), form.ClientSize.Height);
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.StartPosition = FormStartPosition.CenterScreen;
                form.MinimizeBox = false;
                form.MaximizeBox = false;
                form.AcceptButton = buttonOk;
                form.CancelButton = buttonCancel;

                dialogResult = form.ShowDialog();
                a_szValue = textBox.Text;
            }
            catch (Exception exception)
            {
                Log.Error("Something bad happened..." + exception.Message);
            }
            finally
            {
                // On the advice of analyze...
                if (form != null)
                {
                    form.Dispose();
                    form = null;
                }
                if (label != null)
                {
                    label.Dispose();
                    label = null;
                }
                if (textBox != null)
                {
                    textBox.Dispose();
                    textBox = null;
                }
                if (buttonOk != null)
                {
                    buttonOk.Dispose();
                    buttonOk = null;
                }
                if (buttonCancel != null)
                {
                    buttonCancel.Dispose();
                    buttonCancel = null;
                }
            }

            // All done...
            return (dialogResult);
        }

        /// <summary>
        /// Display a message...
        /// </summary>
        /// <param name="a_szMsg">the thing to display</param>
        private void Display(string a_szMsg)
        {
            // Let us be called from any thread...
            if (this.InvokeRequired)
            {
                Invoke(new MethodInvoker(delegate() { Display(a_szMsg); }));
                return;
            }

            // Okay, do the real work...
            m_richtextboxTask.Text += a_szMsg + Environment.NewLine;
            m_richtextboxTask.Select(m_richtextboxTask.Text.Length - 1, 0);
            m_richtextboxTask.ScrollToCaret();
            m_richtextboxTask.Update();
            this.Refresh();
            // This is bad...
            Application.DoEvents();
        }

        #endregion


        ///////////////////////////////////////////////////////////////////////////////
        // Private Form Controls...
        ///////////////////////////////////////////////////////////////////////////////
        #region Private Form Controls...

        /// <summary>
        /// Cleanup...
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            SetButtons(ButtonState.Undefined);
        }

        /// <summary>
        /// Register a device for use...
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void m_buttonRegister_Click(object sender, EventArgs e)
        {
            int iScanner;
            long lResponseCharacterOffset;
            string szNumber;
            string szScanners;
            string szText;
            JsonLookup jsonlookup;
            ApiCmd apicmd;

            // Turn the buttons off...
            SetButtons(ButtonState.Undefined);
            Display("");
            Display("Looking for Scanners (please wait, this can take a while)...");

            // Get the list of scanners...
            szScanners = m_scanner.GetAvailableScanners();
            if (szScanners == null)
            {
                Display("No devices found...");
                return;
            }
            try
            {
                jsonlookup = new JsonLookup();
                jsonlookup.Load(szScanners, out lResponseCharacterOffset);
            }
            catch
            {
                Display("No devices found...");
                return;
            }

            // Show all the scanners, and then ask for the number of
            // the one to use as the new default...
            szText = "";
            for (iScanner = 0 ;; iScanner++)
            {
                // Get the next scanner...
                string szScanner = jsonlookup.Get("scanners[" + iScanner + "].twidentityProductName");
                if (string.IsNullOrEmpty(szScanner))
                {
                    szScanner = jsonlookup.Get("scanners[" + iScanner + "].sane");
                }

                // We're out of stuff...
                if (string.IsNullOrEmpty(szScanner))
                {
                    break;
                }

                // If this is the current default, make a note of it...
                if (m_scanner.GetTwainLocalTy() == szScanner)
                {
                    szText = (iScanner + 1) + ": " + szScanner + " ***DEFAULT***";
                    Display(szText);
                }
                // Otherwise, just list it...
                else
                {
                    Display((iScanner + 1) + ": " + szScanner);
                }
            }

            // Finish the text for the prompt...
            if (string.IsNullOrEmpty(szText))
            {
                szText =
                    "Enter a number from 1 to " + iScanner + Environment.NewLine +
                    "(there is no current default)";
            }
            else
            {
                szText =
                    "Enter a number from 1 to " + iScanner + Environment.NewLine +
                    szText;
            }

            // Select the default...
            int iNumber = 0;
            for (;;)
            {
                // Prompt the user...
                szNumber = "";
                DialogResult dialogresult = InputBox
                (
                    "Select Default Scanner",
                    szText,
                    ref szNumber
                );

                // The user wants out...
                if (dialogresult != DialogResult.OK)
                {
                    Display("Canceled...");
                    break;
                }

                // Check the result...
                if (!int.TryParse(szNumber, out iNumber))
                {
                    Display("Please enter a number in the range 1 to " + iScanner);
                    continue;
                }

                // Check the range...
                if ((iNumber < 1) || (iNumber > iScanner))
                {
                    Display("Please enter a number in the range 1 to " + iScanner);
                    continue;
                }

                // We have what we want...
                break;
            }

            // See if the user wants to update their note...
            string szNote = m_scanner.GetTwainLocalNote();
            if ((iNumber >= 1) && (iNumber <= iScanner))
            {
                DialogResult dialogresult = InputBox
                (
                    "Enter Note",
                    "Your current note is: " + m_scanner.GetTwainLocalNote() + Environment.NewLine +
                    "Type a new note, or just press the Enter key to keep what you have.",
                    ref szNote
                );

                // The user wants out...
                if ((dialogresult != DialogResult.OK) || string.IsNullOrEmpty(szNote))
                {
                    szNote = m_scanner.GetTwainLocalNote();
                }
            }

            // Register it, make a note if it works by clearing the
            // no devices flag...
            apicmd = new ApiCmd();
            if (m_scanner.RegisterScanner(jsonlookup, iNumber, szNote, ref apicmd))
            {
                m_blNoDevices = false;
                Display("Done...");
            }
            else
            {
                Display("Registration failed for: " + iNumber);
            }

            // Fix the buttons...
            Display("Registration done...");
            if (m_blNoDevices)
            {
                SetButtons(ButtonState.NoDevices);
            }
            else
            {
                SetButtons(ButtonState.WaitingForStart);
            }
        }

        /// <summary>
        /// Start polling for work...
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void m_buttonStart_Click(object sender, EventArgs e)
        {
            bool blSuccess;

            // Turn all the buttons off...
            SetButtons(ButtonState.Undefined);

            // Start polling...
            Display("");
            Display("Starting, please wait...");
            blSuccess = m_scanner.MonitorTasksStart();
            if (!blSuccess)
            {
                Log.Error("MonitorTasksStart failed...");
                MessageBox.Show("Error", "Failed to start the device, check the logs for more information.");
                return;
            }
            Display("Ready for use...");

            // Set buttons...
            SetButtons(ButtonState.Started);
        }

        /// <summary>
        /// Stop polling for work...
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void m_buttonStop_Click(object sender, EventArgs e)
        {
            // Turn all the buttons off...
            SetButtons(ButtonState.Undefined);

            // Staaaaaaahp...
            m_scanner.MonitorTasksStop();
            Display("Stop...");

            // Set buttons...
            SetButtons(ButtonState.WaitingForStart);
        }

        #endregion


        ///////////////////////////////////////////////////////////////////////////////
        // Private Definitions...
        ///////////////////////////////////////////////////////////////////////////////
        #region Private Definitions...

        /// <summary>
        /// The button states (enable/disable)...
        /// </summary>
        private enum ButtonState
        {
            Undefined,
            NoDevices,
            WaitingForStart,
            Started
        }

        #endregion


        ///////////////////////////////////////////////////////////////////////////////
        // Private Attributes...
        ///////////////////////////////////////////////////////////////////////////////
        #region Private Attributes...

        /// <summary>
        /// Our scanner interface...
        /// </summary>
        private Scanner m_scanner;

        /// <summary>
        /// True if we have no devices...
        /// </summary>
        private bool m_blNoDevices;

        /// <summary>
        /// Scratchpad for the confirm scan dialog...
        /// </summary>
        private TwainLocalScanner.ButtonPress m_buttonpress;

        #endregion
    }
}
