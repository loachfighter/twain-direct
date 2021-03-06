﻿///////////////////////////////////////////////////////////////////////////////////////
//
// TwainDirect.Support.TwainLocalScanner
// TwainDirect.Support.TwainLocalScanner.TwainLocalSession
//
// Interface to TWAIN Local scanners scanners.  This class is used by applications
// and scanners, since they share enough common features to make it worthwhile to
// consolodate the functionality.  Hopefully, it also helps to make things a little
// more clear as to what's going on.
//
// Functions used by applications are marked as "Client" and functions used by
// scanners are marked as "Device".  Functions common to both have no designation.
//
// There's no obvious reason to expose the Session class, so it's buried inside
// of TwainLocalScanner.  One device can support more than one session, so data
// that's session specific must be located in this class.
//
// ApiCmd is the payload for an ApiCmd command.  We must support multi-threading,
// so we need to be able to pass its objects up and down the stack.  This is why
// it's publically accessible.
//
// It is a central tenet of this class that communication with the device does
// not occur, unless the client believes that communication is warrented.  Therefore
// we test state based on the client's understanding of where it currently is in the
// finite state machine.  This means that it's not impossible to get out of sync
// with the device (though it's unlikely), so we have to confirm in every command
// that we are actually where we expect to be.
//
///////////////////////////////////////////////////////////////////////////////////////
//  Author          Date            Comment
//  M.McLaughlin    15-Oct-2017     Initial Release
///////////////////////////////////////////////////////////////////////////////////////
//  Copyright (C) 2016-2017 Kodak Alaris Inc.
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
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using TwainDirect.Support;
[assembly: CLSCompliant(true)]

// This namespace supports applications and scanners...
namespace TwainDirect.Support
{
    /// <summary>
    /// A scanner interface to a TWAIN Local scanner.  We only support one
    /// registered scanner on a PC.  The user can change this at any time.
    /// The client portion of the class can access any advertised scanner.
    /// </summary>
    public sealed class TwainLocalScanner : IDisposable
    {
        ///////////////////////////////////////////////////////////////////////////////
        // Public Common Methods...
        ///////////////////////////////////////////////////////////////////////////////
        #region Public Common Methods...

        /// <summary>
        /// Init us...
        /// </summary>
        /// <param name="a_confirmscan">user must confirm a scan request</param>
        /// <param name="a_fConfirmScanScale">scale the confirmation dialog</param>
        /// <param name="a_eventcallback">event function</param>
        /// <param name="a_objectEventCallback">object that provided the event</param>
        /// <param name="a_displaycallback">display callback</param>
        /// <param name="a_blCreateTwainLocalSession">true for the server only</param>
        public TwainLocalScanner
        (
            ConfirmScan a_confirmscan,
            float a_fConfirmScanScale,
            EventCallback a_eventcallback,
            object a_objectEventCallback,
            DisplayCallback a_displaycallback,
            bool a_blCreateTwainLocalSession
        )
        {
            int iDefault;

            // Init our command timeout for HTTPS communication...
            iDefault = 15000; // 15 seconds
            m_iHttpTimeoutCommand = (int)Config.Get("httpTimeoutCommand", iDefault);
            if (m_iHttpTimeoutCommand < 5000)
            {
                m_iHttpTimeoutCommand = iDefault;
            }

            // Init our data timeout for HTTPS communication...
            iDefault = 30000; // 30 seconds
            m_iHttpTimeoutData = (int)Config.Get("httpTimeoutData", iDefault);
            if (m_iHttpTimeoutData < 10000)
            {
                m_iHttpTimeoutData = iDefault;
            }

            // Init our event timeout for HTTPS communication...
            iDefault = 30000; // 30 seconds
            m_iHttpTimeoutEvent = (int)Config.Get("httpTimeoutEvent", iDefault);
            if (m_iHttpTimeoutEvent < 10000)
            {
                m_iHttpTimeoutEvent = iDefault;
            }

            // Init our idle session timeout...
            iDefault = 300000; // five minutes
            m_lSessionTimeout = Config.Get("sessionTimeout", iDefault);
            if (m_lSessionTimeout < 10000)
            {
                m_lSessionTimeout = iDefault;
            }

            // Init stuff...
            m_szWriteFolder = Config.Get("writeFolder", "");
            m_confirmscan = a_confirmscan;
            m_fConfirmScanScale = a_fConfirmScanScale;
            m_eventcallback = a_eventcallback;
            m_objectEventCallback = a_objectEventCallback;
            m_displaycallback = a_displaycallback;
            m_szDeviceSecret = Guid.NewGuid().ToString();

            // Set up session specific content...
            if (a_blCreateTwainLocalSession)
            {
                m_twainlocalsessionInfo = new TwainLocalSession("");
            }

            // Our lock...
            m_objectLock = new object();

            // We use this to get notification about events...
            m_autoreseteventWaitForEvents = new AutoResetEvent(false);
            m_autoreseteventWaitForEventsProcessing = new AutoResetEvent(false);

            // This is our default location for storing images...
            try
            {
                m_szImagesFolder = Path.Combine(m_szWriteFolder, "images");
                if (!Directory.Exists(m_szImagesFolder))
                {
                    Directory.CreateDirectory(m_szImagesFolder);
                }
            }
            catch
            {
                throw new Exception("Can't set up an images folder...");
            }

            // Create the timer we'll use for expiring sessions...
            m_timerSession = new Timer(DeviceSessionTimerCallback, this, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>
        /// Destructor...
        /// </summary>
        ~TwainLocalScanner()
        {
            Dispose(false);
        }

        /// <summary>
        /// Cleanup...
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Collect information for our device info...
        /// </summary>
        /// <returns></returns>
        Dnssd.DnssdDeviceInfo GetDnssdDeviceInfo()
        {
            Dnssd.DnssdDeviceInfo dnssddeviceinfo;

            // Create it...
            dnssddeviceinfo = new Dnssd.DnssdDeviceInfo();

            // Stock it...
            dnssddeviceinfo.SetLinkLocal("");
            dnssddeviceinfo.SetServiceName(m_twainlocalsessionInfo.DeviceRegisterGetTwainLocalInstanceName());
            dnssddeviceinfo.SetTxtCs("offline");
            dnssddeviceinfo.SetTxtHttps(true);
            dnssddeviceinfo.SetTxtId("");
            dnssddeviceinfo.SetTxtNote(m_twainlocalsessionInfo.DeviceRegisterGetTwainLocalNote());
            dnssddeviceinfo.SetTxtTxtvers("1");
            dnssddeviceinfo.SetTxtTy(m_twainlocalsessionInfo.DeviceRegisterGetTwainLocalTy());
            dnssddeviceinfo.SetTxtType("twaindirect");

            // Return it...
            return (dnssddeviceinfo);
        }

        /// <summary>
        /// Access the device name
        /// </summary>
        /// <returns>TWAIN Local ty= field</returns>
        public string GetTwainLocalTy()
        {
            return (m_twainlocalsessionInfo.DeviceRegisterGetTwainLocalTy());
        }

        /// <summary>
        /// Return the current images folder...
        /// </summary>
        /// <returns>the images folder</returns>
        public string GetImagesFolder()
        {
            return (m_szImagesFolder);
        }

        /// <summary>
        /// Returns a path for scratch pad use...
        /// </summary>
        /// <param name="a_szFile">an optional file or folder to add to the path</param>
        /// <returns>the path</returns>
        public string GetPath(string a_szFile)
        {
            string szPath = m_szWriteFolder;
            if (!string.IsNullOrEmpty(a_szFile))
            {
                szPath = Path.Combine(szPath, a_szFile);
            }
            if (!Directory.Exists(Path.GetDirectoryName(szPath)))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(szPath));
            }
            return (szPath);
        }

        /// <summary>
        /// Return the current state...
        /// </summary>
        /// <returns>the enum as a string</returns>
        public string GetState()
        {
            if (m_twainlocalsession == null)
            {
                return ("noSession");
            }
            return (m_twainlocalsession.GetSessionState().ToString());
        }

        /// <summary>
        /// Quick access to our platform id.  We probably need a better way to
        /// figure this all out...
        /// </summary>
        /// <returns>a platform</returns>
        public static Platform GetPlatform()
        {
            // First pass...
            if (ms_platform == Platform.UNKNOWN)
            {
                // We're Windows...
                if (Environment.OSVersion.ToString().Contains("Microsoft Windows"))
                {
                    ms_platform = Platform.WINDOWS;
                }

                // We're Mac OS X (this has to come before LINUX!!!)...
                else if (Directory.Exists("/Library/Application Support"))
                {
                    ms_platform = Platform.MACOSX;
                }

                // We're Linux...
                else if (Environment.OSVersion.ToString().Contains("Unix"))
                {
                    ms_platform = Platform.LINUX;
                }

                // We have a problem...
                else
                {
                    ms_platform = Platform.UNKNOWN;
                    throw new Exception("Unsupported platform...");
                }
            }

            // This is it...
            return (ms_platform);
        }

        /// <summary>
        /// Set a new images folder...
        /// </summary>
        /// <param name="a_szImagesFolder">the folder to set</param>
        /// <returns>true on success</returns>
        public bool SetImagesFolder(string a_szImagesFolder)
        {
            // See if we can use it...
            if (!Directory.Exists(a_szImagesFolder))
            {
                try
                {
                    Directory.CreateDirectory(a_szImagesFolder);
                }
                catch (Exception exception)
                {
                    Log.Error("CreateDirectory failed: " + exception.Message);
                    Log.Error("<" + a_szImagesFolder + ">");
                    return (false);
                }
            }

            // We're good...
            m_szImagesFolder = a_szImagesFolder;
            return (true);
        }

        #endregion


        ///////////////////////////////////////////////////////////////////////////////
        // Public Client Methods...
        ///////////////////////////////////////////////////////////////////////////////
        #region Public Client Methods...

        /// <summary>
        /// TWAIN Direct has four layers of error reporting: there's the HTTP status
        /// codes; a security layer defined by Privet 1.0; a TWAIN Local protocol
        /// layer, and a TWAIN Direct language layer.
        /// 
        /// This function checks out the health of the communication across all four
        /// layers, and updates the ApiCmd object with the results.  It returns true
        /// if no errors were found.
        /// </summary>
        /// <param name="a_szFunction">function we're reporting on</param>
        /// <param name="a_apicmd">object to update if we find problems</param>
        /// <returns>true on success, false if an error was detected</returns>
        public bool ClientCheckForApiErrors(string a_szFunction, ref ApiCmd a_apicmd)
        {
            int ii;
            bool blSuccess;
            long lJsonErrorIndex;
            string szReply;
            string szAction;
            string szSuccess;
            string szCode;
            string szJsonKey;
            string szCharacterOffset;
            string szDescription;
            JsonLookup jsonlookup;

            // First, let's check on the health of the HTTP communication.
            // If this fails, close the session...
            if (a_apicmd.HttpStatus() != System.Net.WebExceptionStatus.Success)
            {
                a_apicmd.SetApiErrorFacility(ApiCmd.ApiErrorFacility.httpstatus);
                a_apicmd.AddApiErrorCode("httpError");
                a_apicmd.AddApiErrorDescription(a_szFunction + ": httpError - " + a_apicmd.HttpStatus());
                return (false);
            }

            // Check the response data, every command must come back with a JSON
            // payload, so if we don't have one, that's a protocol error...
            szReply = a_apicmd.GetResponseData();
            if (string.IsNullOrEmpty(szReply))
            {
                a_apicmd.SetApiErrorFacility(ApiCmd.ApiErrorFacility.protocol);
                a_apicmd.AddApiErrorCode("protocolError");
                a_apicmd.AddApiErrorDescription(a_szFunction + ": protocolError - data is missing from the HTTP response");
                return (false);
            }

            // If the JSON format of the reply is damaged, we have a protocol
            // error...
            jsonlookup = new JsonLookup();
            blSuccess = jsonlookup.Load(szReply, out lJsonErrorIndex);
            if (!blSuccess)
            {
                a_apicmd.SetApiErrorFacility(ApiCmd.ApiErrorFacility.protocol);
                a_apicmd.AddApiErrorCode("invalidJson");
                a_apicmd.AddApiErrorDescription(a_szFunction + ": protocolError - JSON error in the HTTP response at character offset " + lJsonErrorIndex);
                return (false);
            }

            // Do we have a security error?
            szCode = jsonlookup.Get("error", false);
            if (!string.IsNullOrEmpty(szCode))
            {
                szDescription = jsonlookup.Get("description", false);
                if (string.IsNullOrEmpty(szDescription))
                {
                    szDescription = "(no description provided)";
                }
                a_apicmd.SetApiErrorFacility(ApiCmd.ApiErrorFacility.security);
                a_apicmd.AddApiErrorCode("invalidJson");
                a_apicmd.AddApiErrorDescription(a_szFunction + ": " + szCode + " - " + szDescription);
                return (false);
            }

            // How about a protocol error?
            szSuccess = jsonlookup.Get("results.success", false);
            if (string.IsNullOrEmpty(szSuccess) || ((szSuccess != "false") && (szSuccess != "true")))
            {
                a_apicmd.SetApiErrorFacility(ApiCmd.ApiErrorFacility.protocol);
                a_apicmd.AddApiErrorCode("protocolError");
                a_apicmd.AddApiErrorDescription(a_szFunction + ": protocolError - results.success is missing or invalid");
                return (false);
            }
            else if (szSuccess == "false")
            {
                // Collect the data...
                szCode = jsonlookup.Get("results.code", false);
                if (string.IsNullOrEmpty(szCode))
                {
                    szCode = "invalidTask";
                }
                szJsonKey = jsonlookup.Get("results.jsonKey", false);
                if (string.IsNullOrEmpty(szJsonKey))
                {
                    szJsonKey = "(n/a)";
                }
                szCharacterOffset = jsonlookup.Get("results.characterOffset", false);
                if (string.IsNullOrEmpty(szCharacterOffset))
                {
                    szCharacterOffset = "(n/a)";
                }

                // Squirrel it away...
                a_apicmd.SetApiErrorFacility(ApiCmd.ApiErrorFacility.protocol);
                a_apicmd.AddApiErrorCode("protocolError");
                a_apicmd.AddApiErrorDescription(a_szFunction + ": " + szCode + " - characterOffset=" + szCharacterOffset + " jsonKey=" + szJsonKey);
                return (false);
            }

            // All that's left are language errors, and those are
            // catagorized with each action.  Report all of them...
            bool blErrorDetected = false;
            for (ii = 0; true; ii++)
            {
                // If we run out of actions, scoot...
                szAction = "results.session.task.actions[" + ii + "]";
                szSuccess = jsonlookup.Get(szAction, false);
                if (string.IsNullOrEmpty(szSuccess))
                {
                    break;
                }

                // We'd better have one of these...
                szSuccess = jsonlookup.Get(szAction + ".results.success", false);

                // We're good, check the next one...
                if (!string.IsNullOrEmpty(szSuccess) && (szSuccess == "true"))
                {
                    continue;
                }

                // We've found a problem child...
                blErrorDetected = true;

                // The value better be false...
                if (string.IsNullOrEmpty(szSuccess) || (szSuccess != "false"))
                {
                    a_apicmd.SetApiErrorFacility(ApiCmd.ApiErrorFacility.language);
                    a_apicmd.AddApiErrorCode("invalidTask");
                    a_apicmd.AddApiErrorDescription(a_szFunction + ": invalidTask - " + szAction + ".results.success is missing or invalid");
                    continue;
                }

                // Collect the data on it...
                szCode = jsonlookup.Get(szAction + ".results.code", false);
                if (string.IsNullOrEmpty(szCode))
                {
                    szCode = "invalidTask";
                }
                szJsonKey = jsonlookup.Get(szAction + ".results.jsonKey", false);
                if (string.IsNullOrEmpty(szJsonKey))
                {
                    szJsonKey = "(n/a)";
                }

                // Add it to our list...
                a_apicmd.SetApiErrorFacility(ApiCmd.ApiErrorFacility.language);
                a_apicmd.AddApiErrorCode("invalidTask");
                a_apicmd.AddApiErrorDescription(a_szFunction + ": " + szCode + " - " + szAction + ", jsonKey=" + szJsonKey);
            }
            if (blErrorDetected)
            {
                return (false);
            }

            // Golly, it looks like we're in good shape...
            return (true);
        }

        /// <summary>
        /// Return the current image blocks...
        /// </summary>
        /// <returns>array of image blocks</returns>
        public long[] ClientGetImageBlocks()
        {
            return (m_twainlocalsession.m_alSessionImageBlocks);
        }

        /// <summary>
        /// Return the imageBlocksDrained flag...
        /// </summary>
        /// <returns>true if the scanner has no more images</returns>
        public bool ClientGetImageBlocksDrained()
        {
            return (m_twainlocalsession.GetSessionImageBlocksDrained());
        }

        /// <summary>
        /// Return the current image blocks...
        /// </summary>
        /// <returns>array of image blocks</returns>
        public string ClientGetImageBlocks(ApiCmd a_apicmd)
        {
            return (a_apicmd.GetImageBlocks().Replace(" ",""));
        }

        /// <summary>
        /// Return the current session state...
        /// </summary>
        /// <returns>session state</returns>
        public string ClientGetSessionState()
        {
            if (m_twainlocalsession == null)
            {
                return ("noSession");
            }
            return (m_twainlocalsession.GetSessionState().ToString());
        }

        /// <summary>
        /// Tell us about the health of the session...
        /// </summary>
        /// <param name="a_szDetected"></param>
        /// <returns></returns>
        public bool ClientGetSessionStatusSuccess(out string a_szDetected)
        {
            if (m_twainlocalsession == null)
            {
                a_szDetected = "nominal";
                return (true);
            }
            a_szDetected = m_twainlocalsession.GetSessionStatusDetected();
            return (m_twainlocalsession.GetSessionStatusSuccess());
        }

        /// <summary>
        /// Get info about the device...
        /// </summary>
        /// <param name="a_apicmd">info about the command</param>
        /// <param name="a_szOverride">used for certification testing</param>
        /// <returns>true on success</returns>
        public bool ClientInfo
        (
            ref ApiCmd a_apicmd,
            string a_szOverride = null
        )
        {
            bool blSuccess;
            string szCommand;
            string szFunction = "ClientInfo";

            // This command can be issued at any time, so we don't check state, we also
            // don't have to worry about locking anything...

            // Figure out what command we're sending...
            if (a_szOverride != null)
            {
                szCommand = "/privet/" + a_szOverride;
            }
            else
            {
                szCommand = (Config.Get("useInfoex", "yes") == "yes") ? "/privet/infoex" : "/privet/info";
            }

            // Send the RESTful API command, we'll support using either
            // privet/info or /privet/infoex, but we'll default to infoex...
            blSuccess = ClientHttpRequest
            (
                szFunction,
                ref a_apicmd,
                szCommand,
                "GET",
                ClientHttpBuildHeader(true),
                null,
                null,
                null,
                m_iHttpTimeoutCommand,
                ApiCmd.HttpReplyStyle.SimpleReplyWithSessionInfo
            );
            if (!blSuccess)
            {
                ClientReturnError(a_apicmd, false, "", 0, "");
                return (false);
            }

            // All done...
            return (true);
        }

        // The naming convention for this bit is Executer / Package / Command.  So, since
        // this is the client section, the executer is the Client.  The TWAIN Local package is
        // "scanner" and the commands are TWAIN Direct Client-Scanner API commands.  If you want to find
        // the corresponding function used by scanners, just replace "Client" with "Device"...

        /// <summary>
        /// Close a session...
        /// </summary>
        /// <param name="a_apicmd">info about the command</param>
        /// <returns>true on success</returns>
        public bool ClientScannerCloseSession(ref ApiCmd a_apicmd)
        {
            bool blSuccess;
            string szFunction = "ClientScannerCloseSession";

            // Lock this command to protect the session object...
            lock (m_objectLock)
            {
                string szClientCreateCommandId = "";
                string szSessionId = "";

                // Collect session data, if we have any...
                if (m_twainlocalsession != null)
                {
                    szClientCreateCommandId = m_twainlocalsession.ClientCreateCommandId();
                    szSessionId = m_twainlocalsession.GetSessionId();
                }

                // Send the RESTful API command...
                blSuccess = ClientHttpRequest
                (
                    szFunction,
                    ref a_apicmd,
                    "/privet/twaindirect/session",
                    "POST",
                    ClientHttpBuildHeader(),
                    "{" +
                    "\"kind\":\"twainlocalscanner\"," +
                    "\"commandId\":\"" + szClientCreateCommandId + "\"," +
                    "\"method\":\"closeSession\"," +
                    "\"params\":{" +
                    "\"sessionId\":\"" + szSessionId + "\"" +
                    "}" +
                    "}",
                    null,
                    null,
                    m_iHttpTimeoutCommand,
                    ApiCmd.HttpReplyStyle.SimpleReply
                );
                if (!blSuccess)
                {
                    ClientReturnError(a_apicmd, false, "", 0, "");
                    return (false);
                }

                // A session can be closed with pending imageBlocks, in which case
                // it's in a closed state, but it can't transition to NoSession
                // until all of the images have been released...
                if (    (m_twainlocalsession.m_alSessionImageBlocks == null)
                    ||  (m_twainlocalsession.m_alSessionImageBlocks.Length == 0))
                {
                    SetSessionState(SessionState.noSession);
                }
                else
                {
                    SetSessionState(SessionState.closed);
                }
            }

            // All done...
            return (true);
        }

        /// <summary>
        /// Create a session, basically seeing if the device is available for use.
        /// If it works out the session state will go to "ready".  Anything else
        /// is going to be an issue...
        /// </summary>
        /// <param name="a_apicmd">info about the command</param>
        /// <returns>true on success</returns>
        public bool ClientScannerCreateSession(ref ApiCmd a_apicmd)
        {
            bool blSuccess;
            bool blCreatedTwainLocalSession = false;
            string szFunction = "ClientScannerCreateSession";

            // Lock this command to protect the session object...
            lock (m_objectLock)
            {
                // Create it if we need it...
                if (m_twainlocalsession == null)
                {
                    // We got this X-Privet-Token from info or infoex, if we didn't
                    // get one yet, there will be sadness on the scanner side...
                    m_twainlocalsession = new TwainLocalSession(m_szXPrivetToken);
                    blCreatedTwainLocalSession = true;
                }

                // Send the RESTful API command...
                blSuccess = ClientHttpRequest
                (
                    szFunction,
                    ref a_apicmd,
                    "/privet/twaindirect/session",
                    "POST",
                    ClientHttpBuildHeader(),
                    "{" +
                    "\"kind\":\"twainlocalscanner\"," +
                    "\"commandId\":\"" + m_twainlocalsession.ClientCreateCommandId() + "\"," +
                    "\"method\":\"createSession\"" +
                    "}",
                    null,
                    null,
                    m_iHttpTimeoutCommand,
                    ApiCmd.HttpReplyStyle.SimpleReplyWithSessionInfo
                );
                if (!blSuccess)
                {
                    ClientReturnError(a_apicmd, false, "", 0, "");
                    if (blCreatedTwainLocalSession)
                    {
                        m_twainlocalsession.Dispose();
                        m_twainlocalsession = null;
                    }
                    return (false);
                }

                // Set our state (to get this far, things must be okay)...
                SetSessionState(SessionState.ready);
            }

            // All done...
            return (true);
        }

        /// <summary>
        /// Get the session information...
        /// </summary>
        /// <param name="a_apicmd">info about the command</param>
        /// <returns>true on success</returns>
        public bool ClientScannerGetSession(ref ApiCmd a_apicmd)
        {
            bool blSuccess;
            string szFunction = "ClientScannerGetSession";

            // Lock this command to protect the session object...
            lock (m_objectLock)
            {
                string szClientCreateCommandId = "";
                string szSessionId = "";

                // Collection session data, if we have any...
                if (m_twainlocalsession != null)
                {
                    szClientCreateCommandId = m_twainlocalsession.ClientCreateCommandId();
                    szSessionId = m_twainlocalsession.GetSessionId();
                }

                // Send the RESTful API command...
                blSuccess = ClientHttpRequest
                (
                    szFunction,
                    ref a_apicmd,
                    "/privet/twaindirect/session",
                    "POST",
                    ClientHttpBuildHeader(),
                    "{" +
                    "\"kind\":\"twainlocalscanner\"," +
                    "\"commandId\":\"" + szClientCreateCommandId + "\"," +
                    "\"method\":\"getSession\"," +
                    "\"params\":{" +
                    "\"sessionId\":\"" + szSessionId + "\"" +
                    "}" +
                    "}",
                    null,
                    null,
                    m_iHttpTimeoutCommand,
                    ApiCmd.HttpReplyStyle.SimpleReplyWithSessionInfo
                );
                if (!blSuccess)
                {
                    ClientReturnError(a_apicmd, false, "", 0, "");
                    return (false);
                }
            }

            // All done...
            return (true);
        }

        /// <summary>
        /// This is an invalid command, it's only used to test certification, please
        /// don't go around adding this to your applications... >.<
        /// </summary>
        /// <param name="a_apicmd">info about the command</param>
        /// <returns>true on success</returns>
        public bool ClientScannerInvalidCommand(ref ApiCmd a_apicmd)
        {
            bool blSuccess;
            string szFunction = "ClientScannerInvalidCommand";

            // Lock this command to protect the session object...
            lock (m_objectLock)
            {
                // Send the RESTful API command...
                blSuccess = ClientHttpRequest
                (
                    szFunction,
                    ref a_apicmd,
                    "/privet/twaindirect/session",
                    "POST",
                    ClientHttpBuildHeader(),
                    "{" +
                    "\"kind\":\"twainlocalscanner\"," +
                    "\"commandId\":\"" + Guid.NewGuid().ToString() + "\"," +
                    "\"method\":\"invalidCommand\"" +
                    "}",
                    null,
                    null,
                    m_iHttpTimeoutCommand,
                    ApiCmd.HttpReplyStyle.SimpleReply
                );
                if (!blSuccess)
                {
                    ClientReturnError(a_apicmd, false, "", 0, "");
                    return (false);
                }
            }

            // All done...
            return (true);
        }

        /// <summary>
        /// This is an invalid uri, it's only used to test certification, please
        /// don't go around adding this to your applications... >.<
        /// </summary>
        /// <param name="a_apicmd">info about the command</param>
        /// <returns>true on success</returns>
        public bool ClientScannerInvalidUri(ref ApiCmd a_apicmd)
        {
            bool blSuccess;
            string szFunction = "ClientScannerInvalidUri";

            // Lock this command to protect the session object...
            lock (m_objectLock)
            {
                // Send the RESTful API command...
                blSuccess = ClientHttpRequest
                (
                    szFunction,
                    ref a_apicmd,
                    "/privet/twaindirect/invaliduri",
                    "GET",
                    ClientHttpBuildHeader(true),
                    null,
                    null,
                    null,
                    m_iHttpTimeoutCommand,
                    ApiCmd.HttpReplyStyle.SimpleReply
                );
                if (!blSuccess)
                {
                    ClientReturnError(a_apicmd, false, "", 0, "");
                    return (false);
                }
            }

            // All done...
            return (true);
        }

        /// <summary>
        /// Read an image block from the scanner...
        /// </summary>
        /// <param name="a_lImageBlockNum">block number to read</param>
        /// <param name="a_blGetMetadataWithImage">ask for the metadata</param>
        /// <param name="a_scancallback">function to call</param>
        /// <param name="a_apicmd">info about the command</param>
        /// <returns>true on success</returns>
        public bool ClientScannerReadImageBlock
        (
            long a_lImageBlockNum,
            bool a_blGetMetadataWithImage,
            ScanCallback a_scancallback,
            ref ApiCmd a_apicmd
        )
        {
            bool blSuccess;
            string szImage;
            string szMetaFile;
            string szFunction = "ClientScannerReadImageBlock";

            // Lock this command to protect the session object...
            lock (m_objectLock)
            {
                string szClientCreateCommandId = "";
                string szSessionId = "";

                // Collection session data, if we have any...
                if (m_twainlocalsession != null)
                {
                    szClientCreateCommandId = m_twainlocalsession.ClientCreateCommandId();
                    szSessionId = m_twainlocalsession.GetSessionId();
                }

                // Build the full image path...
                szImage = Path.Combine(m_szImagesFolder, "img" + a_lImageBlockNum.ToString("D6") + ".pdf");

                // Make sure it's clean...
                if (File.Exists(szImage))
                {
                    try
                    {
                        File.Delete(szImage);
                    }
                    catch
                    {
                        ClientReturnError(a_apicmd, false, "critical", -1, szFunction + ": access denied: " + szImage);
                        return (false);
                    }
                }

                // Send the RESTful API command...
                blSuccess = ClientHttpRequest
                (
                    szFunction,
                    ref a_apicmd,
                    "/privet/twaindirect/session",
                    "POST",
                    ClientHttpBuildHeader(),
                    "{" +
                    "\"kind\":\"twainlocalscanner\"," +
                    "\"commandId\":\"" + szClientCreateCommandId + "\"," +
                    "\"method\":\"readImageBlock\"," +
                    "\"params\":{" +
                    "\"sessionId\":\"" + szSessionId + "\"," +
                    (a_blGetMetadataWithImage ? "\"withMetadata\":true," : "") +
                    "\"imageBlockNum\":" + a_lImageBlockNum +
                    "}" +
                    "}",
                    null,
                    szImage,
                    m_iHttpTimeoutData,
                    ApiCmd.HttpReplyStyle.SimpleReplyWithSessionInfo
                );
                if (!blSuccess)
                {
                    ClientReturnError(a_apicmd, false, "", 0, "");
                    return (false);
                }

                // We asked for metadata...
                if (a_blGetMetadataWithImage && (m_twainlocalsession != null))
                {
                    // Try to get the meta data...
                    if (string.IsNullOrEmpty(m_twainlocalsession.GetMetadata()))
                    {
                        m_twainlocalsession.SetMetadata(null);
                        ClientReturnError(a_apicmd, false, "critical", -1, szFunction + ": 'results.metadata' missing");
                        return (false);
                    }

                    // Save the metadata to a file...
                    szMetaFile = Path.Combine(m_szImagesFolder, "img" + a_lImageBlockNum.ToString("D6") + ".meta");
                    try
                    {
                        File.WriteAllText(szMetaFile, "{\"metadata\":" + m_twainlocalsession.GetMetadata() + "}");
                    }
                    catch (Exception exception)
                    {
                        m_twainlocalsession.SetMetadata(null);
                        ClientReturnError(a_apicmd, false, "critical", -1, szFunction + " access denied: " + szMetaFile + " (" + exception.Message + ")");
                        return (false);
                    }
                    Log.Info("metadata: " + szMetaFile);
                }

                // If we have a scanner callback, hit it now...
                if (a_scancallback != null)
                {
                    a_scancallback(szImage);
                }
            }

            // All done...
            Log.Info("image: " + szImage);
            return (true);
        }

        /// <summary>
        /// Read an image block's TWAIN Direct metadata from the scanner...
        /// </summary>
        /// <param name="a_lImageBlockNum">image block to read</param>
        /// <param name="a_blGetThumbnail">the caller would like a thumbnail</param>
        /// <param name="a_scancallback">function to call</param>
        /// <param name="a_apicmd">info about the command</param>
        /// <returns>true on success</returns>
        public bool ClientScannerReadImageBlockMetadata(long a_lImageBlockNum, bool a_blGetThumbnail, ScanCallback a_scancallback, ref ApiCmd a_apicmd)
        {
            bool blSuccess;
            string szThumbnail;
            string szMetaFile = "(no session)";
            string szFunction = "ClientScannerReadImageBlockMetadata";

            // Lock this command to protect the session object...
            lock (m_objectLock)
            {
                string szClientCreateCommandId = "";
                string szSessionId = "";

                // Collection session data, if we have any...
                if (m_twainlocalsession != null)
                {
                    szClientCreateCommandId = m_twainlocalsession.ClientCreateCommandId();
                    szSessionId = m_twainlocalsession.GetSessionId();
                }

                // We're asking for a thumbnail...
                szThumbnail = null;
                if (a_blGetThumbnail)
                {
                    // Build the full image thumbnail path...
                    szThumbnail = Path.Combine(m_szImagesFolder, "img" + a_lImageBlockNum.ToString("D6") + "_thumbnail.pdf");

                    // Make sure it's clean...
                    if (File.Exists(szThumbnail))
                    {
                        try
                        {
                            File.Delete(szThumbnail);
                        }
                        catch (Exception exception)
                        {
                            ClientReturnError(a_apicmd, false, "critical", -1, szFunction + ": access denied: " + szThumbnail + " (" + exception.Message + ")");
                            return (false);
                        }
                    }
                }

                // Send the RESTful API command...
                blSuccess = ClientHttpRequest
                (
                    szFunction,
                    ref a_apicmd,
                    "/privet/twaindirect/session",
                    "POST",
                    ClientHttpBuildHeader(),
                    "{" +
                    "\"kind\":\"twainlocalscanner\"," +
                    "\"commandId\":\"" + szClientCreateCommandId + "\"," +
                    "\"method\":\"readImageBlockMetadata\"," +
                    "\"params\":{" +
                    "\"sessionId\":\"" + szSessionId + "\"," +
                    "\"imageBlockNum\":" + a_lImageBlockNum +
                    (a_blGetThumbnail ? ",\"withThumbnail\":true" : "") +
                    "}" +
                    "}",
                    null,
                    a_blGetThumbnail ? szThumbnail : null,
                    m_iHttpTimeoutCommand,
                    ApiCmd.HttpReplyStyle.SimpleReplyWithSessionInfo
                );
                if (!blSuccess)
                {
                    ClientReturnError(a_apicmd, false, "", 0, "");
                    return (false);
                }

                // Make sure we have a session for this...
                if (m_twainlocalsession != null)
                {
                    // Try to get the meta data...
                    if (string.IsNullOrEmpty(m_twainlocalsession.GetMetadata()))
                    {
                        m_twainlocalsession.SetMetadata(null);
                        ClientReturnError(a_apicmd, false, "critical", -1, szFunction + " 'results.metadata' missing");
                        return (false);
                    }

                    // Save the metadata to a file...
                    szMetaFile = Path.Combine(m_szImagesFolder, "img" + a_lImageBlockNum.ToString("D6") + ".meta");
                    try
                    {
                        File.WriteAllText(szMetaFile, "{\"metadata\":" + m_twainlocalsession.GetMetadata() + "}");
                    }
                    catch (Exception exception)
                    {
                        m_twainlocalsession.SetMetadata(null);
                        ClientReturnError(a_apicmd, false, "critical", -1, szFunction + " access denied: " + szMetaFile + " (" + exception.Message + ")");
                        return (false);
                    }

                    // Give it to the callback...
                    if (a_scancallback != null)
                    {
                        a_scancallback(szMetaFile);
                    }
                }
            }

            // All done...
            Log.Info("metadata:  " + szMetaFile);
            if (a_blGetThumbnail)
            {
                Log.Info("thumbnail: " + szThumbnail);
            }
            return (true);
        }

        /// <summary>
        /// Release one or more image blocks
        /// </summary>
        /// <param name="a_lImageBlockNum">first block to release</param>
        /// <param name="a_lLastImageBlockNum">last block in range (inclusive)</param>
        /// <param name="a_apicmd">info about the command</param>
        /// <returns></returns>
        public bool ClientScannerReleaseImageBlocks(long a_lImageBlockNum, long a_lLastImageBlockNum, ref ApiCmd a_apicmd)
        {
            bool blSuccess;
            string szFunction = "ClientScannerReleaseImageBlocks";

            // Lock this command to protect the session object...
            lock (m_objectLock)
            {
                string szClientCreateCommandId = "";
                string szSessionId = "";

                // Collection session data, if we have any...
                if (m_twainlocalsession != null)
                {
                    szClientCreateCommandId = m_twainlocalsession.ClientCreateCommandId();
                    szSessionId = m_twainlocalsession.GetSessionId();
                }

                // Send the RESTful API command...
                blSuccess = ClientHttpRequest
                (
                    szFunction,
                    ref a_apicmd,
                    "/privet/twaindirect/session",
                    "POST",
                    ClientHttpBuildHeader(),
                    "{" +
                    "\"kind\":\"twainlocalscanner\"," +
                    "\"commandId\":\"" + szClientCreateCommandId + "\"," +
                    "\"method\":\"releaseImageBlocks\"," +
                    "\"params\":{" +
                    "\"sessionId\":\"" + szSessionId + "\"," +
                    "\"imageBlockNum\":" + a_lImageBlockNum + "," +
                    "\"lastImageBlockNum\":" + a_lLastImageBlockNum +
                    "}" +
                    "}",
                    null,
                    null,
                    m_iHttpTimeoutCommand,
                    ApiCmd.HttpReplyStyle.SimpleReplyWithSessionInfo
                );
                if (!blSuccess)
                {
                    ClientReturnError(a_apicmd, false, "", 0, "");
                    return (false);
                }

                // If closeSession was previously called, we can be in a closed
                // state, until all of the image blocks have been released.  We'll
                // also go noSession if our m_twainlocalsession has gone bye-bye...
                if (    (m_twainlocalsession == null)
                    ||  ((m_twainlocalsession.GetSessionState() == SessionState.closed)
                    &&  ((m_twainlocalsession.m_alSessionImageBlocks == null) || (m_twainlocalsession.m_alSessionImageBlocks.Length == 0))))
                {
                    SetSessionState(SessionState.noSession);
                }

                // If stopCapturing was previously called, we can be in a draining
                // state, until all of the image blocks have been released...
                else if (   (m_twainlocalsession.GetSessionState() == SessionState.draining)
                         && ((m_twainlocalsession.m_alSessionImageBlocks == null)
                         || (m_twainlocalsession.m_alSessionImageBlocks.Length == 0)))
                {
                    SetSessionState(SessionState.ready);
                }
            }

            // All done...
            return (true);
        }

        /// <summary>
        /// Send a task to the scanner...
        /// </summary>
        /// <param name="a_szTask">the task to use</param>
        /// <param name="a_apicmd">info about the command</param>
        /// <returns>true on success</returns>
        public bool ClientScannerSendTask(string a_szTask, ref ApiCmd a_apicmd)
        {
            bool blSuccess;
            string szFunction = "ClientScannerSendTask";

            // Lock this command to protect the session object...
            lock (m_objectLock)
            {
                string szClientCreateCommandId = "";
                string szSessionId = "";

                // Collection session data, if we have any...
                if (m_twainlocalsession != null)
                {
                    szClientCreateCommandId = m_twainlocalsession.ClientCreateCommandId();
                    szSessionId = m_twainlocalsession.GetSessionId();
                }

                // Send the RESTful API command...
                blSuccess = ClientHttpRequest
                (
                    szFunction,
                    ref a_apicmd,
                    "/privet/twaindirect/session",
                    "POST",
                    ClientHttpBuildHeader(),
                    "{" +
                    "\"kind\":\"twainlocalscanner\"," +
                    "\"commandId\":\"" + szClientCreateCommandId + "\"," +
                    "\"method\":\"sendTask\"," +
                    "\"params\":{" +
                    "\"sessionId\":\"" + szSessionId + "\"," +
                    "\"task\":" + a_szTask +
                    "}" +
                    "}",
                    null,
                    null,
                    m_iHttpTimeoutCommand,
                    ApiCmd.HttpReplyStyle.SimpleReplyWithSessionInfo
                );
                if (!blSuccess)
                {
                    ClientReturnError(a_apicmd, false, "", 0, "");
                    return (false);
                }
            }

            // All done...
            return (true);
        }

        /// <summary>
        /// Start capturing...
        /// </summary>
        /// <param name="a_apicmd">info about the command</param>
        /// <returns>true on success</returns>
        public bool ClientScannerStartCapturing(ref ApiCmd a_apicmd)
        {
            bool blSuccess;
            string szFunction = "ClientScannerStartCapturing";

            // Lock this command to protect the session object...
            lock (m_objectLock)
            {
                string szClientCreateCommandId = "";
                string szSessionId = "";

                // Collection session data, if we have any...
                if (m_twainlocalsession != null)
                {
                    szClientCreateCommandId = m_twainlocalsession.ClientCreateCommandId();
                    szSessionId = m_twainlocalsession.GetSessionId();
                    m_twainlocalsession.SetSessionStatusSuccess(true);
                    m_twainlocalsession.SetSessionStatusDetected("nominal");
                }

                // Send the RESTful API command...
                blSuccess = ClientHttpRequest
                (
                    szFunction,
                    ref a_apicmd,
                    "/privet/twaindirect/session",
                    "POST",
                    ClientHttpBuildHeader(),
                    "{" +
                    "\"kind\":\"twainlocalscanner\"," +
                    "\"commandId\":\"" + szClientCreateCommandId + "\"," +
                    "\"method\":\"startCapturing\"," +
                    "\"params\":{" +
                    "\"sessionId\":\"" + szSessionId + "\"" +
                    "}" +
                    "}",
                    null,
                    null,
                    m_iHttpTimeoutCommand,
                    ApiCmd.HttpReplyStyle.SimpleReplyWithSessionInfo
                );
                if (!blSuccess)
                {
                    ClientReturnError(a_apicmd, false, "", 0, "");
                    if (m_twainlocalsession != null)
                    {
                        m_twainlocalsession.SetSessionImageBlocksDrained(true);
                    }
                    return (false);
                }
            }

            // All done...
            return (true);
        }

        /// <summary>
        /// Stop capturing...
        /// </summary>
        /// <param name="a_apicmd">info about the command</param>
        /// <returns>true on success</returns>
        public bool ClientScannerStopCapturing(ref ApiCmd a_apicmd)
        {
            bool blSuccess;
            string szFunction = "ClientScannerStopCapturing";

            // Lock this command to protect the session object...
            lock (m_objectLock)
            {
                string szClientCreateCommandId = "";
                string szSessionId = "";

                // Collection session data, if we have any...
                if (m_twainlocalsession != null)
                {
                    szClientCreateCommandId = m_twainlocalsession.ClientCreateCommandId();
                    szSessionId = m_twainlocalsession.GetSessionId();
                }

                // Send the RESTful API command...
                blSuccess = ClientHttpRequest
                (
                    szFunction,
                    ref a_apicmd,
                    "/privet/twaindirect/session",
                    "POST",
                    ClientHttpBuildHeader(),
                    "{" +
                    "\"kind\":\"twainlocalscanner\"," +
                    "\"commandId\":\"" + szClientCreateCommandId + "\"," +
                    "\"method\":\"stopCapturing\"," +
                    "\"params\":{" +
                    "\"sessionId\":\"" + szSessionId + "\"" +
                    "}" +
                    "}",
                    null,
                    null,
                    m_iHttpTimeoutCommand,
                    ApiCmd.HttpReplyStyle.SimpleReplyWithSessionInfo
                );
                if (!blSuccess)
                {
                    ClientReturnError(a_apicmd, false, "", 0, "");
                    return (false);
                }
            }

            // All done...
            return (true);
        }

        /// <summary>
        /// Wait for one or more events...
        /// </summary>
        /// <param name="a_apicmd">info about the command</param>
        /// <returns>true on success</returns>
        public bool ClientScannerWaitForEvents(ref ApiCmd a_apicmd)
        {
            bool blSuccess;
            string szFunction = "ClientScannerWaitForEvents";

            // Lock this command to protect the session object...
            lock (m_objectLock)
            {
                string szSessionId = "";

                // Collection session data, if we have any...
                if (m_twainlocalsession != null)
                {
                    szSessionId = m_twainlocalsession.GetSessionId();
                }

                // Send the RESTful API command...
                // Both @@@COMMANDID@@@ and @@@SESSIONREVISION@@@ are resolved
                // inside of the ClientScannerWaitForEventsHelper thread...
                blSuccess = ClientHttpRequest
                (
                    szFunction,
                    ref a_apicmd,
                    "/privet/twaindirect/session",
                    "POST",
                    ClientHttpBuildHeader(),
                    "{" +
                    "\"kind\":\"twainlocalscanner\"," +
                    "\"commandId\":\"@@@COMMANDID@@@\"," +
                    "\"method\":\"waitForEvents\"," +
                    "\"params\":{" +
                    "\"sessionId\":\"" + szSessionId + "\"," +
                    "\"sessionRevision\":@@@SESSIONREVISION@@@" +
                    "}" +
                    "}",
                    null,
                    null,
                    m_iHttpTimeoutEvent,
                    ApiCmd.HttpReplyStyle.Event
                );
                if (!blSuccess)
                {
                    ClientReturnError(a_apicmd, false, "", 0, "");
                    return (false);
                }
            }

            // All done...
            return (true);
        }

        /// <summary>
        /// Allow us to kick ourselves out of a wait...
        /// </summary>
        /// <returns></returns>
        public void ClientWaitForSessionUpdateForceSet()
        {
            if (m_twainlocalsession != null)
            {
                m_twainlocalsession.ClientWaitForSessionUpdateForceSet();
            }
        }

        /// <summary>
        /// Wait for the session object to be updated, this is done
        /// by comparing the current session.revision number to the
        /// session.revision from the last command or event.
        /// </summary>
        /// <param name="a_lMilliseconds">milliseconds to wait for the update</param>
        /// <returns>true if an update was detected, false if the command timed out or was aborted</returns>
        public bool ClientWaitForSessionUpdate(long a_lMilliseconds)
        {
            bool blSignaled = false;

            // Wait for it...
            if (m_twainlocalsession != null)
            {
                blSignaled = m_twainlocalsession.ClientWaitForSessionUpdate(a_lMilliseconds);
            }

            // All done...
            Log.Info("ClientWaitForSessionUpdate - " + (blSignaled ? "true" : "false"));
            return (blSignaled);
        }

        /// <summary>
        /// Create a TWAIN Local Session object.  If this hasn't been done yet you
        /// can specify the X-Privet-Token for testing.  The following have special
        /// meaning:
        /// no_header - no X-Privet-Header
        /// no_token - X-Privet-Header with no data
        /// anything else is droppedin verbatim
        /// </summary>
        /// <param name="a_szXPrivetToken">token to use if we don't have one yet</param>
        public void ClientCertificationTwainLocalSessionCreate
        (
            string a_szXPrivetToken = "no_token"
        )
        {
            if (m_twainlocalsession == null)
            {
                // If we have a token from info or infoex, use it, otherwise make a bad token,
                // as the function indicates this is for the client only, and in fact it's just
                // for the certification tool...
                m_twainlocalsession = new TwainLocalSession(string.IsNullOrEmpty(m_szXPrivetToken) ? a_szXPrivetToken : m_szXPrivetToken);
            }
        }

        /// <summary>
        /// Destroy a TWAIN Local Session object
        /// </summary>
        public void ClientCertificationTwainLocalSessionDestroy()
        {
            if (m_twainlocalsession != null)
            {
                m_twainlocalsession.Dispose();
                m_twainlocalsession = null;
            }
        }

        #endregion


        ///////////////////////////////////////////////////////////////////////////////
        // Public Device Methods...
        ///////////////////////////////////////////////////////////////////////////////
        #region Public Device Methods...

        /// <summary>
        /// Dispatch a command...
        /// </summary>
        /// <param name="a_szJsonCommand">the command we received</param>
        /// <param name="a_httplistenercontext">thr HTTP object that delivered the command</param>
        /// <returns>true on success</returns>
        public void DeviceDispatchCommand(string a_szJsonCommand, ref HttpListenerContext a_httplistenercontext)
        {
            int ii;
            bool blSuccess;
            int iTaskIndex;
            ApiCmd apicmd;
            string szUri;
            string szXPrivetToken;
            string szFunction = "DeviceDispatchCommand";

            // Confirm that this command is coming in on a good URI, if it's not
            // then ignore it...
            szUri = a_httplistenercontext.Request.RawUrl.ToString();
            if (    (szUri != "/privet/info")
                &&  (szUri != "/privet/infoex")
                &&  (szUri != "/privet/twaindirect/session"))
            {
                return;
            }

            // Every command must have X-Privet-Token in the header...
            for (ii = 0; ii < a_httplistenercontext.Request.Headers.Count; ii++)
            {
                if (a_httplistenercontext.Request.Headers.GetKey(ii) == "X-Privet-Token")
                {
                    break;
                }
            }
            if (ii >= a_httplistenercontext.Request.Headers.Count)
            {
                apicmd = new ApiCmd(null, null, ref a_httplistenercontext);
                DeviceReturnError(szFunction, apicmd, "invalid_x_privet_token", null, 0);
                return;
            }

            // We found X-Privet-Token, squirrel away the value, remove any double quotes...
            szXPrivetToken = a_httplistenercontext.Request.Headers.Get(ii).Replace("\"","");

            // Handle the /privet/info and /privet/infoex commands...
            if (    (szUri == "/privet/info")
                ||  (szUri == "/privet/infoex"))
            {
                // Log it...
                Log.Info("");
                Log.Info("http>>> " + szUri.Replace("/privet/",""));
                Log.Info("http>>> " + a_httplistenercontext.Request.HttpMethod + " uri " + a_httplistenercontext.Request.Url.AbsoluteUri);

                // Get each header and display each value.
                NameValueCollection namevaluecollectionHeaders = a_httplistenercontext.Request.Headers;
                foreach (string szKey in namevaluecollectionHeaders.AllKeys)
                {
                    string[] aszValues = namevaluecollectionHeaders.GetValues(szKey);
                    if (aszValues.Length == 0)
                    {
                        Log.Verbose("http>>> recvheader " + szKey + ": n/a");
                    }
                    else
                    {
                        foreach (string szValue in aszValues)
                        {
                            Log.Verbose("http>>> recvheader " + szKey + ": " + szValue);
                        }
                    }
                }

                // Run it...
                apicmd = new ApiCmd(null, null, ref a_httplistenercontext);
                DeviceInfo(ref apicmd);
                return;
            }

            // If we get here, it implies that a command has been issued before making
            // a call to info or infoex, so we'll reject it.  This is technically a
            // state violation, but invalid_x_privet_token takes priority...
            if (string.IsNullOrEmpty(szXPrivetToken))
            {
                apicmd = new ApiCmd(null, null, ref a_httplistenercontext);
                DeviceReturnError(szFunction, apicmd, "invalid_x_privet_token", null, -1);
                return;
            }

            // The rest of this must be coming in on /privet/twaindirect/session,
            // we'll start by validating our X-Privet-Token.  We check the session
            // first, because if it has the token, it wins...
            else if ((m_twainlocalsession != null) && (szXPrivetToken == m_twainlocalsession.GetXPrivetToken()))
            {
                // Woot! We're good, keep going...
            }

            // We should only come here if we don't have a session with a token,
            // which means this should be a createSession command...
            else
            {
                bool blValid = false;
                long lXPrivetTokenTicks;

                // Crack the token open, if it looks valid, and if its timestamp falls
                // inside of our window, we'll take it.  The window is small, just 30
                // seconds, but it can be overridden, if needed...
                if (!string.IsNullOrEmpty(szXPrivetToken))
                {
                    // Get at the ticks...
                    string[] aszTokens = szXPrivetToken.Split(new string[] { ":" }, StringSplitOptions.None);
                    if ((aszTokens != null) && (aszTokens.Length == 2) && (aszTokens[1] != null) && long.TryParse(aszTokens[1], out lXPrivetTokenTicks))
                    {
                        // Check the ticks against our current tick count...
                        long lCurrentTicks = DateTime.Now.Ticks;
                        if ((lCurrentTicks >= lXPrivetTokenTicks) && (((lCurrentTicks - lXPrivetTokenTicks) / TimeSpan.TicksPerSecond) < Config.Get("createSessionWindow", 30000)))
                        {
                            // So far so good, now see if we can generate the same token
                            // from the data we have...
                            string szTest = CreateXPrivetToken(lXPrivetTokenTicks);
                            if (szXPrivetToken == szTest)
                            {
                                blValid = true;
                            }
                        }
                    }
                }

                // Nope, we're done...
                if (!blValid)
                {
                    apicmd = new ApiCmd(null, null, ref a_httplistenercontext);
                    DeviceReturnError(szFunction, apicmd, "invalid_x_privet_token", null, 0);
                    return;
                }
            }

            // Parse the command...
            long lResponseCharacterOffset;
            JsonLookup jsonlookup = new JsonLookup();
            blSuccess = jsonlookup.Load(a_szJsonCommand, out lResponseCharacterOffset);
            if (!blSuccess)
            {
                apicmd = new ApiCmd(null, jsonlookup, ref a_httplistenercontext);
                DeviceReturnError(szFunction, apicmd, "invalidJson", null, lResponseCharacterOffset);
                return;
            }

            // Init stuff...
            apicmd = new ApiCmd(null, jsonlookup, ref a_httplistenercontext);

            // If we are running a session, make sure that the command's session id matches
            // our session's id...
            lock (m_objectLock)
            {
                // If we have no session, and we're not processing "createSession" then
                // we have a problem.  We can get here if the session timeout was hit...
                if ((m_twainlocalsession == null) && (jsonlookup.Get("method") != "createSession"))
                {
                    Log.Error(szFunction + ": sessionId error: <" + jsonlookup.Get("params.sessionId") + "> <(no session)>");
                    DeviceReturnError(szFunction, apicmd, "invalidSessionId", null, -1);
                    return;
                }

                // If we have a session, and the command is "createSession", then we're
                // busy, so bug off...
                if ((m_twainlocalsession != null) && (jsonlookup.Get("method") == "createSession"))
                {
                    Log.Error(szFunction + ": busy, we're already running a session");
                    DeviceReturnError(szFunction, apicmd, "busy", null, -1);
                    return;
                }

                // If we have a session, the call must match our sessionId...
                if ((m_twainlocalsession != null) && !string.IsNullOrEmpty(m_twainlocalsession.GetSessionId()))
                {
                    if (jsonlookup.Get("params.sessionId") != m_twainlocalsession.GetSessionId())
                    {
                        Log.Error(szFunction + ": sessionId error: <" + jsonlookup.Get("params.sessionId") + "> <" + m_twainlocalsession.GetSessionId() + ">");
                        DeviceReturnError(szFunction, apicmd, "invalidSessionId", null, -1);
                        return;
                    }
                }
            }

            // Log it...
            if (Log.GetLevel() != 0)
            {
                Log.Info("");
                Log.Info("http>>> " + jsonlookup.Get("method"));
                Log.Info("http>>> " + a_httplistenercontext.Request.HttpMethod + " uri " + a_httplistenercontext.Request.Url.AbsoluteUri);
                NameValueCollection namevaluecollectionHeaders = a_httplistenercontext.Request.Headers;
                // Get each header and display each value.
                foreach (string szKey in namevaluecollectionHeaders.AllKeys)
                {
                    string[] aszValues = namevaluecollectionHeaders.GetValues(szKey);
                    if (aszValues.Length == 0)
                    {
                        Log.Verbose("http>>> recvheader " + szKey + ": n/a");
                    }
                    else
                    {
                        foreach (string szValue in aszValues)
                        {
                            Log.Verbose("http>>> recvheader " + szKey + ": " + szValue);
                        }
                    }
                }
                Log.Info("http>>> recvdata " + a_szJsonCommand);
            }

            // Dispatch the command...
            switch (jsonlookup.Get("method"))
            {
                default:
                    break;

                case "closeSession":
                    DeviceScannerCloseSession(ref apicmd);
                    break;

                case "createSession":
                    DeviceScannerCreateSession(ref apicmd, szXPrivetToken);
                    break;

                case "getSession":
                    DeviceScannerGetSession(ref apicmd, false, false, null);
                    break;

                case "readImageBlock":
                    DeviceScannerReadImageBlock(ref apicmd);
                    break;

                case "readImageBlockMetadata":
                    DeviceScannerReadImageBlockMetadata(ref apicmd);
                    break;

                case "releaseImageBlocks":
                    DeviceScannerReleaseImageBlocks(ref apicmd);
                    break;

                case "sendTask":
                    // The task must be an object, we'll treat this as a JSON error,
                    // even though it's syntactically okay.  If the type is undefined
                    // it means we didn't find a task.
                    switch (jsonlookup.GetType("params.task"))
                    {
                        // We found the property, and it's an object, so drop down...
                        case JsonLookup.EPROPERTYTYPE.OBJECT:
                            break;

                        // We didn't find the property...
                        case JsonLookup.EPROPERTYTYPE.UNDEFINED:
                            Log.Error(szFunction + ": JSON property is missing...");
                            DeviceReturnError(szFunction, apicmd, "invalidJson", null, 0);
                            return;

                        // We found the property, but it's not an object...
                        default:
                            iTaskIndex = a_szJsonCommand.IndexOf("\"task\":") + 7;
                            Log.Error(szFunction + ": JSON must be an object...");
                            DeviceReturnError(szFunction, apicmd, "invalidJson", null, iTaskIndex);
                            return;
                    }

                    // Go ahead and process it...
                    DeviceScannerSendTask(ref apicmd);
                    break;

                case "startCapturing":
                    // No prompt...
                    if (m_confirmscan == null)
                    {
                        DeviceScannerStartCapturing(ref apicmd);
                    }
                    // Prompt the user to begin scanning...
                    else
                    {
                        ButtonPress buttonpress = m_confirmscan(m_fConfirmScanScale);
                        if (buttonpress == ButtonPress.OK)
                        {
                            DeviceScannerStartCapturing(ref apicmd);
                        }
                    }
                    break;

                case "stopCapturing":
                    DeviceScannerStopCapturing(ref apicmd);
                    break;

                case "waitForEvents":
                    DeviceScannerWaitForEvents(ref apicmd);
                    break;
            }

            // All done...
            return;
        }

        /// <summary>
        /// Start monitoring for HTTP commands...
        /// </summary>
        /// <returns></returns>
        public bool DeviceHttpServerStart()
        {
            int iPort;
            bool blSuccess;

            // Get our port...
            if (!int.TryParse(Config.Get("usePort","55555"), out iPort))
            {
                Log.Error("DeviceHttpServerStart: bad port..." + Config.Get("usePort", "55555"));
                return (false);
            }

            // Validate values, note is optional, so we don't test it...
            if (string.IsNullOrEmpty(m_twainlocalsessionInfo.DeviceRegisterGetTwainLocalInstanceName()))
            {
                Log.Error("DeviceHttpServerStart: bad instance name...");
                return (false);
            }
            if (iPort == 0)
            {
                Log.Error("DeviceHttpServerStart: bad port...");
                return (false);
            }
            if (string.IsNullOrEmpty(m_twainlocalsessionInfo.DeviceRegisterGetTwainLocalTy()))
            {
                Log.Error("DeviceHttpServerStart: bad ty...");
                return (false);
            }

            // Create our server...
            m_httpserver = new HttpServer();

            // Start us up...
            blSuccess = m_httpserver.ServerStart
            (
                DeviceDispatchCommand,
                m_twainlocalsessionInfo.DeviceRegisterGetTwainLocalInstanceName(),
                iPort,
                m_twainlocalsessionInfo.DeviceRegisterGetTwainLocalTy(),
                "",
                m_twainlocalsessionInfo.DeviceRegisterGetTwainLocalNote()
            );
            if (!blSuccess)
            {
                Log.Error("ServerStart failed...");
                return (false);
            }

            // All done...
            return (true);
        }

        /// <summary>
        /// Stop monitoring for HTTP commands...
        /// </summary>
        /// <returns></returns>
        public void DeviceHttpServerStop()
        {
            if (m_httpserver != null)
            {
                m_httpserver.ServerStop();
                m_httpserver = null;
            }
        }

        /// <summary>
        /// Register a device.
        /// 
        /// We register the commands and finalize.  None of this requires anything
        /// more than our application key.
        /// </summary>
        /// <param name="a_jsonlookup">the twain driver info</param>
        /// <param name="a_iScanner">the index of the driver we want to register</param>
        /// <param name="a_szNote">a note for this scanner from the user</param>
        /// <param name="a_apicmd">info about the command</param>
        /// <returns>true on success</returns>
        public bool DeviceRegister(JsonLookup a_jsonlookup, int a_iScanner, string a_szNote, ref ApiCmd a_apicmd)
        {
            // We're being asked to clear the register...
            if (a_iScanner < 0)
            {
                m_twainlocalsessionInfo.DeviceRegisterClear();
                return (true);
            }

            // Get the scanner entry...
            string szScanner = "scanners[" + a_iScanner + "]";

            // Collect our data...
            string szDeviceName = a_jsonlookup.Get(szScanner + ".twidentityProductName");
            if (string.IsNullOrEmpty(szDeviceName))
            {
                szDeviceName = a_jsonlookup.Get(szScanner + ".sane");
            }
            string szHostName = a_jsonlookup.Get(szScanner + ".hostName");
            string szSerialNumber = a_jsonlookup.Get(szScanner + ".serialNumber");
            string szScannerRecord = a_jsonlookup.Get(szScanner);

            // Set the register.txt file...
            try
            {
                m_twainlocalsessionInfo.DeviceRegisterSet(szDeviceName, szSerialNumber, a_szNote, szScannerRecord);
            }
            catch
            {
                ClientReturnError(a_apicmd, false, "invalidJason", -1, "DeviceRegister: JSON syntax error...");
                return (false);
            }

            // All done...
            return (true);
        }

        /// <summary>
        /// Load the register data from a file...
        /// </summary>
        /// <returns>true on success</returns>
        public bool DeviceRegisterLoad()
        {
            // First load the data...
            if (!m_twainlocalsessionInfo.DeviceRegisterLoad(this, Path.Combine(m_szWriteFolder, "register.txt")))
            {
                return (false);
            }

            // All done...
            return (true);
        }

        /// <summary>
        /// Save the register data to a file...
        /// </summary>
        /// <returns>true on success</returns>
        public bool DeviceRegisterSave()
        {
            return (m_twainlocalsessionInfo.DeviceRegisterSave(Path.Combine(m_szWriteFolder, "register.txt")));
        }

        /// <summary>
        /// The pending HTTP command for long poll events...
        /// </summary>
        /// <returns>the object</returns>
        public ApiCmd GetApiCmdEvent()
        {
            return (m_apicmdEvent);
        }

        /// <summary>
        /// Return the note= field...
        /// </summary>
        /// <returns>users friendly name</returns>
        public string GetTwainLocalNote()
        {
            return (m_twainlocalsessionInfo.DeviceRegisterGetTwainLocalNote());
        }

        #endregion


        ///////////////////////////////////////////////////////////////////////////////
        // Public Definitions...
        ///////////////////////////////////////////////////////////////////////////////
        #region Public Definitions...

        /// <summary>
        /// Our supported platforms...
        /// </summary>
        public enum Platform
        {
            UNKNOWN,
            WINDOWS,
            LINUX,
            MACOSX
        };

        /// <summary>
        /// Buttons that a user can press...
        /// </summary>
        public enum ButtonPress
        {
            OK,
            Cancel
        };

        /// <summary>
        /// TWAIN Direct Client-Scanner API errors.  Be sure to only append to the list,
        /// treat the numbers as unmodifiable constants, so that we can
        /// guarantee their value across interfaces...
        /// </summary>
        public enum ApiStatus
        {
            success = 0,
            newSessionNotAllowed = 1,
            invalidSessionId = 2,
            closedSession = 3,
            notReady = 4,
            notCapturing = 5,
            invalidImageBlockNumber = 6,
            invalidCapturingOptions = 7
        }

        /// <summary>
        /// A place to keep our command information...
        /// </summary>
        public struct Command
        {
            public string szDeviceName;
            public string szJson;
        }

        /// <summary>
        /// Delegate for the long poll event processor.  This function
        /// is called for every event received from the scanner.
        /// </summary>
        /// <param name="a_object">An object supplied by the caller when it registers the callback</param>
        public delegate void WaitForEventsProcessingCallback(ApiCmd a_apicmd, object a_object);

        /// <summary>
        /// Delegate for the scan callback...
        /// </summary>
        /// <param name="a_szImage"></param>
        /// <returns></returns>
        public delegate bool ScanCallback(string a_szImage);

        /// <summary>
        /// Delegate for event callback...
        /// </summary>
        /// <param name="a_object">caller's object</param>
        /// <param name="a_szEvent">event</param>
        public delegate void EventCallback(object a_object, string a_szEvent);

        /// <summary>
        /// Prompt the user to confirm a request to scan...
        /// </summary>
        /// <returns>button the user pressed</returns>
        public delegate ButtonPress ConfirmScan(float a_fConfirmScanScale);

        /// <summary>
        /// Display callback...
        /// </summary>
        /// <param name="a_szText">text to display</param>
        public delegate void DisplayCallback(string a_szText);

        #endregion


        ///////////////////////////////////////////////////////////////////////////////
        // Private Common methods...
        ///////////////////////////////////////////////////////////////////////////////
        #region Private Common Methods...

        /// <summary>
        /// Display a message, if we have a callback for it...
        /// </summary>
        /// <param name="a_szMsg">the message to display</param>
        private void Display(string a_szMsg)
        {
            if (m_displaycallback != null)
            {
                m_displaycallback(a_szMsg);
            }
        }

        /// <summary>
        /// Set the session state, and do additional cleanup work, if needed...
        /// </summary>
        /// <param name="a_sessionstate">new session state</param>
        /// <param name="a_szSessionEndedMessage">message to display when going to noSession</param>
        /// <returns>the previous session state</returns>
        private SessionState SetSessionState
        (
            SessionState a_sessionstate,
            string a_szSessionEndedMessage = "Session ended..."
        )
        {
            SessionState sessionstatePrevious = SessionState.noSession;

            // First set the session's state...
            if (m_twainlocalsession != null)
            {
                sessionstatePrevious = m_twainlocalsession.GetSessionState();
                m_twainlocalsession.SetSessionState(a_sessionstate);
            }

            // Cleanup...
            if (a_sessionstate == SessionState.noSession)
            {
                // Lose the eventing stuff on the client side...
                if (m_waitforeventsinfo != null)
                {
                    m_waitforeventsinfo.Dispose();
                    m_waitforeventsinfo = null;
                }

                // Lose the eventing stuff on the device side...
                if (m_apicmdEvent != null)
                {
                    m_apicmdEvent.HttpAbort();
                    m_apicmdEvent = null;
                }

                // Lose the session...
                if (m_twainlocalsession != null)
                {
                    m_twainlocalsession.Dispose();
                    m_twainlocalsession = null;
                }

                // Lose the timer...
                if (m_timerSession != null)
                {
                    m_timerSession.Change(Timeout.Infinite, Timeout.Infinite);
                }

                // Display what happened...
                Display(a_szSessionEndedMessage);
            }

            // Return the previous state...
            return (sessionstatePrevious);
        }

        /// <summary>
        /// Set the error return for client functions...
        /// </summary>
        /// <param name="a_apicmd">the current command</param>
        /// <param name="a_blSuccess">our success status</param>
        /// <param name="a_szResponseCode">the error code</param>
        /// <param name="a_lResponseCharacterOffset">the offset of a JSON error, or -1</param>
        /// <param name="a_szResponseText">extra info about the error</param>
        private void ClientReturnError(ApiCmd a_apicmd, bool a_blSuccess, string a_szResponseCode, long a_lResponseCharacterOffset, string a_szResponseText)
        {
            long lJsonErrorIndex;

            // Only log something if we have something...
            if (!string.IsNullOrEmpty(a_szResponseText))
            {
                Log.Error(a_szResponseText);
            }

            // Handle protocol errors...
            if (a_apicmd.GetResponseStatus() != 200)
            {
                string szResponseText = a_apicmd.GetResponseText();
                if (string.IsNullOrEmpty(szResponseText))
                {
                    a_apicmd.DeviceResponseSetStatus(false, "protocolError", 0, "unrecognized protocol error, sorry.");
                }
                else
                {
                    JsonLookup jsonlookup = new JsonLookup();
                    jsonlookup.Load(szResponseText, out lJsonErrorIndex);
                    string szError = jsonlookup.Get("error");
                    if (string.IsNullOrEmpty(szError))
                    {
                        szError = "protocolError";
                    }
                    a_apicmd.DeviceResponseSetStatus(false, szError, 0, szResponseText);
                }

                // All done...
                return;
            }

            // Set the command's error return...
            a_apicmd.DeviceResponseSetStatus(a_blSuccess, a_szResponseCode, a_lResponseCharacterOffset, a_szResponseText);
        }

        /// <summary>
        /// Cleanup...
        /// </summary>
        /// <param name="a_blDisposing">true if we need to clean up managed resources</param>
        internal void Dispose(bool a_blDisposing)
        {
            // Free managed resources...
            if (a_blDisposing)
            {
                if (m_timerSession != null)
                {
                    m_timerSession.Change(Timeout.Infinite, Timeout.Infinite);
                    m_timerSession.Dispose();
                    m_timerSession = null;
                }
                if (m_autoreseteventWaitForEvents != null)
                {
                    m_autoreseteventWaitForEvents.Dispose();
                    m_autoreseteventWaitForEvents = null;
                }
                if (m_autoreseteventWaitForEventsProcessing != null)
                {
                    m_autoreseteventWaitForEventsProcessing.Dispose();
                    m_autoreseteventWaitForEventsProcessing = null;
                }
                if (m_waitforeventsinfo != null)
                {
                    m_waitforeventsinfo.Dispose();
                    m_waitforeventsinfo = null;
                }
                if (m_httpserver != null)
                {
                    m_httpserver.Dispose();
                    m_httpserver = null;
                }
                if (m_twainlocalsession != null)
                {
                    m_twainlocalsession.Dispose();
                    m_twainlocalsession = null;
                }
                if (m_twainlocalsessionInfo != null)
                {
                    m_twainlocalsessionInfo.Dispose();
                    m_twainlocalsessionInfo = null;
                }
            }
        }

        /// <summary>
        /// Convert a C# DateTime to a Unix Epoch-based timestamp in milliseconds...
        /// </summary>
        /// <param name="dateTime">value to convert</param>
        /// <returns>result</returns>
        public static long DateTimeToUnixTimeMs(DateTime a_datetime)
        {
            DateTime datetimeUnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
            long u64Ticks = (long)(a_datetime.ToUniversalTime() - datetimeUnixEpoch).Ticks;
            return (u64Ticks / TimeSpan.TicksPerMillisecond);
        }

        // Run curl...get the stdout as a string, log the command and the result...
        private string Run(string szProgram, string a_szArguments)
        {
            // Log what we're doing...
            Log.Info("run>>> " + szProgram);
            Log.Info("run>>> " + a_szArguments);

            // Start the child process.
            Process p = new Process();

            // Redirect the output stream of the child process.
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.WorkingDirectory = Path.GetDirectoryName(szProgram);
            p.StartInfo.CreateNoWindow = true;
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.FileName = szProgram;
            p.StartInfo.Arguments = a_szArguments;
            p.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            p.Start();

            // Do not wait for the child process to exit before
            // reading to the end of its redirected stream.
            // p.WaitForExit();
            // Read the output stream first and then wait.
            string szOutput = p.StandardOutput.ReadToEnd();
            p.WaitForExit();

            // Log any output...
            Log.Info("run>>> " + szOutput);

            // All done...
            return (szOutput);
        }

        /// <summary>
        /// Convert a Unix Epoch-based timestamp in milliseconds to a C# DateTime...
        /// </summary>
        /// <param name="a_lUnixTimeMs">value to convert</param>
        /// <returns>result</returns>
        public static DateTime UnixTimeMsToDateTime(long a_lUnixTimeMs)
        {
            DateTime datetimeUnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
            long u64Ticks = (long)(a_lUnixTimeMs * TimeSpan.TicksPerMillisecond);
            return (new DateTime(datetimeUnixEpoch.Ticks + u64Ticks));
        }

        /// <summary>
        /// Parse data from the session object.  We do this in a number
        /// of places, so it makes sense to centralize...
        /// </summary>
        /// <param name="a_httpreplystyle">the reply style</param>
        /// <param name="a_apicmd">the command object</param>
        /// <param name="a_jsonlookup">data to check</param>
        /// <returns>true on success</returns>
        private bool ParseSession(string a_szReason, ApiCmd a_apicmd, out string a_szCode)
        {
            bool blSuccess;
            bool blIsInfoOrInfoex;
            int ii;
            int iSessionRevision;
            long lResponseCharacterOffset;
            string szImageBlocks;
            string szFunction = "ParseSession";
            JsonLookup jsonlookup;

            // Parse the JSON in the response, we always have to make sure
            // its valid...
            jsonlookup = new JsonLookup();
            blSuccess = jsonlookup.Load(a_apicmd.HttpResponseData(), out lResponseCharacterOffset);
            if (!blSuccess)
            {
                ClientReturnError(a_apicmd, false, "invalidJson", lResponseCharacterOffset, a_szReason + ": ClientHttpRequest JSON syntax error...");
                a_szCode = "critical";
                return (false);
            }

            // Are we info or infoex?
            blIsInfoOrInfoex = ((a_apicmd.GetUri() == "/privet/info") || (a_apicmd.GetUri() == "/privet/infoex"));

            // Run-roh...
            if (!blIsInfoOrInfoex && jsonlookup.Get("results.success") == "false")
            {
                // Get the code...
                a_szCode = jsonlookup.Get("results.code");
                if (string.IsNullOrEmpty(a_szCode))
                {
                    Log.Error("results.code is missing, so we're assuming 'critical'...");
                    a_szCode = "critical";
                }

                // If we've lost the session, we might as well zap things here...
                switch (a_szCode)
                {
                    default: break;
                    case "critical": SetSessionState(SessionState.noSession); break;
                    case "invalidSessionId": SetSessionState(SessionState.noSession); break;
                }

                // Bail...
                return (false);
            }

            // We expect success from this point on, unless set otherwise...
            a_szCode = "success";

            // If we done't have one of these styles, we can't have any session
            // data, so bail here...
            if (    (a_apicmd.GetHttpReplyStyle() != ApiCmd.HttpReplyStyle.SimpleReplyWithSessionInfo)
                &&  (a_apicmd.GetHttpReplyStyle() != ApiCmd.HttpReplyStyle.Event))
            {
                return (true);
            }

            // Is this /privet/info or /privet/infoex?
            if (blIsInfoOrInfoex)
            {
                // Squirrel away the x-privet-token so that we'll have
                // it for when createSession is called.  Note that this
                // is only done for the client, the device never does
                // anything with this attribute...
                m_szXPrivetToken = jsonlookup.Get("x-privet-token");

                // All done...
                return (true);
            }

            // Handle events...
            if (a_apicmd.GetHttpReplyStyle() == ApiCmd.HttpReplyStyle.Event)
            {
                // Handle any and all of the event data...
                if (!string.IsNullOrEmpty(jsonlookup.Get("results.events", false)))
                {
                    // Loop through it...
                    for (ii = 0 ;; ii++)
                    {
                        string szEvent = "results.events[" + ii + "]";

                        // We're out of events...
                        if (string.IsNullOrEmpty(jsonlookup.Get(szEvent, false)))
                        {
                            break;
                        }

                        // Process this event...
                        switch (jsonlookup.Get(szEvent + ".event", false))
                        {
                            // Ignore unrecognized events...
                            default:
                                Log.Verbose(szFunction + ": unrecognized event..." + jsonlookup.Get(szEvent + ".event", false));
                                break;

                            // The session object has been updated, specifically we have a change
                            // to the imageBlocks array from stuff being added or removed...
                            case "imageBlocks":
                                Log.Verbose(szFunction + ": imageBlocks event...");
                                lock (m_objectLock)
                                {
                                    // Check the session revision number...
                                    if (!int.TryParse(jsonlookup.Get(szEvent + ".session.revision", false), out iSessionRevision))
                                    {
                                        Log.Error(szFunction + ": bad session revision number...");
                                        continue;
                                    }

                                    // Only do this bit if the number is newer than what
                                    // we already have...
                                    if (!m_twainlocalsession.SetSessionRevision(iSessionRevision, true))
                                    {
                                        continue;
                                    }

                                    // Only set the imageBlocksDrained to true if we're told to,
                                    // don't set them to false.  That's done once during the state
                                    // change to capturing...
                                    if (jsonlookup.Get(szEvent + ".session.imageBlocksDrained", false) == "true")
                                    {
                                        m_twainlocalsession.SetSessionImageBlocksDrained(true);
                                    }

                                    // Get the image blocks...
                                    m_twainlocalsession.m_alSessionImageBlocks = null;
                                    szImageBlocks = jsonlookup.Get(szEvent + ".session.imageBlocks", false);
                                    if (!string.IsNullOrEmpty(szImageBlocks))
                                    {
                                        string[] aszImageBlocks = szImageBlocks.Split(new char[] { '[', ' ', ',', ']', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                                        if (aszImageBlocks != null)
                                        {
                                            m_twainlocalsession.m_alSessionImageBlocks = new long[aszImageBlocks.Length];
                                            for (ii = 0; ii < aszImageBlocks.Length; ii++)
                                            {
                                                m_twainlocalsession.m_alSessionImageBlocks[ii] = long.Parse(aszImageBlocks[ii]);
                                            }
                                        }
                                    }

                                    // See if we've detected any new problems...
                                    if (m_twainlocalsession.GetSessionStatusSuccess())
                                    {
                                        string szSessionStatusSuccess = jsonlookup.Get(szEvent + ".session.status.success", false);
                                        if (!string.IsNullOrEmpty(szSessionStatusSuccess) && (szSessionStatusSuccess == "false"))
                                        {
                                            string szSessionStatusDetected = jsonlookup.Get(szEvent + ".session.status.detected", false);
                                            m_twainlocalsession.SetSessionStatusSuccess(false);
                                            m_twainlocalsession.SetSessionStatusDetected(string.IsNullOrEmpty(szSessionStatusDetected) ? "misfeed" : szSessionStatusDetected);
                                        }
                                    }

                                    // Wakeup anybody watching us...
                                    ClientWaitForSessionUpdateForceSet();
                                }
                                break;

                            // Our scanner session went bye-bye on us...
                            case "sessionTimedOut":
                                // Our reply...
                                Log.Info(szFunction + ": sessionTimedOut event...");
                                if (m_eventcallback != null)
                                {
                                    m_eventcallback(m_objectEventCallback, "sessionTimedOut");
                                }

                                // Wake up anybody watching us...
                                ClientWaitForSessionUpdateForceSet();

                                // Give them a couple of seconds...
                                //Thread.Sleep(2000);

                                // Now we can zap it...
                                //SetSessionState(SessionState.noSession);

                                // All done...
                                break;
                        }
                    }
                }

                // All done...
                return (true);
            }

            // Init stuff...
            m_twainlocalsession.SetSessionId(jsonlookup.Get("results.session.sessionId"));
            m_twainlocalsession.m_alSessionImageBlocks = null;

            // Set the metadata, if we have any, we don't care if we
            // succeed, the caller will worry about that...
            m_twainlocalsession.SetMetadata(jsonlookup.Get("results.metadata", false));

            // If we don't have a session id, then skip the rest of
            // this function...
            if (string.IsNullOrEmpty(m_twainlocalsession.GetSessionId()))
            {
                a_szCode = "invalidSessionId";
                return (false);
            }

            // Check the session revision number...
            if (!int.TryParse(jsonlookup.Get("results.session.revision", false), out iSessionRevision))
            {
                Log.Error(szFunction + ": bad session revision number...");
                a_szCode = "critical";
                return (false);
            }

            // If the session revision number we just received is less
            // than or equal to the one we already have, then skip the
            // rest of this function.  Otherwise, save the number...
            if (!m_twainlocalsession.SetSessionRevision(iSessionRevision))
            {
                return (true);
            }

            // Protect ourselves from weirdness...
            try
            {
                // Only set the imageBlocksDrained to true if we're told to,
                // don't set them to false.  That's done once during the state
                // change to capturing...
                if (jsonlookup.Get("results.session.imageBlocksDrained", false) == "true")
                {
                    m_twainlocalsession.SetSessionImageBlocksDrained(true);
                }

                // Collect the image blocks data...
                m_twainlocalsession.m_alSessionImageBlocks = null;
                szImageBlocks = jsonlookup.Get("results.session.imageBlocks", false);
                if (!string.IsNullOrEmpty(szImageBlocks))
                {
                    string[] aszImageBlocks = szImageBlocks.Split(new char[] { '[', ' ', ',', ']', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    if (aszImageBlocks != null)
                    {
                        m_twainlocalsession.m_alSessionImageBlocks = new long[aszImageBlocks.Length];
                        for (ii = 0; ii < aszImageBlocks.Length; ii++)
                        {
                            m_twainlocalsession.m_alSessionImageBlocks[ii] = int.Parse(aszImageBlocks[ii]);
                        }
                    }
                }

                // Change our state...
                switch (jsonlookup.Get("results.session.state"))
                {
                    // Uh-oh...
                    default:
                        Log.Error(szFunction + ":Unrecognized results.session.state..." + jsonlookup.Get("results.session.state"));
                        a_szCode = "critical";
                        return (false);

                    case "capturing":
                        SetSessionState(SessionState.capturing);
                        break;

                    case "closed":
                        // We can't truly close until all the imageblocks are resolved...
                        if (    (m_twainlocalsession.m_alSessionImageBlocks == null)
                            ||  (m_twainlocalsession.m_alSessionImageBlocks.Length == 0))
                        {
                            SetSessionState(SessionState.noSession);
                        }
                        else
                        {
                            SetSessionState(SessionState.closed);
                        }
                        break;

                    case "draining":
                        SetSessionState(SessionState.draining);
                        break;

                    case "nosession":
                        SetSessionState(SessionState.noSession);
                        break;

                    case "ready":
                        SetSessionState(SessionState.ready);
                        break;
                }
            }
            catch (Exception exception)
            {
                Log.Error(szFunction + ": exception..." + exception.Message);
                m_twainlocalsession.SetSessionId(null);
                m_twainlocalsession.SetCallersHostName(null);
                m_twainlocalsession.m_alSessionImageBlocks = null;
                m_twainlocalsession.SetSessionImageBlocksDrained(true);
                a_szCode = "critical";
                return (false);
            }

            // All done...
            return (true);
        }

        #endregion


        ///////////////////////////////////////////////////////////////////////////////
        // Private Client Methods...
        ///////////////////////////////////////////////////////////////////////////////
        #region Private Client Methods...

        /// <summary>
        /// Build the HTTP headers needed by the client.  I didn't want to write this
        /// function, because it makes something simple too complex, but I needed it
        /// to help with the certification tool.  Application writers just need to
        /// make sure they set the headers for info/infoex, and the POST commands
        /// sent using the TWAIN Local RESTful API.
        ///
        /// I've commented the code with what apps do vs what the certification tool
        /// needs...
        /// </summary>
        /// <param name="a_blInfoInfoex">build header for info/infoex</param>
        /// <returns>the HTTP headers</returns>
        private string[] ClientHttpBuildHeader(bool a_blInfoInfoex = false)
        {
            string[] aszHeader;
            string szXPrivetToken = "";
            string szContentType = "Content-Type: application/json; charset=UTF-8";

            // Apps do this: collect session data, if we have any...
            if (m_twainlocalsession != null)
            {
                szXPrivetToken = m_twainlocalsession.GetXPrivetToken();

                // Certification only: test the scanner when we don't give it an
                // X-Privet-Token...
                if (szXPrivetToken == "no-token")
                {
                    szXPrivetToken = null;
                }
            }

            // Apps do this: if we're issuing info/infoex command, then we have
            // no content-type and the caller will set the argument to null
            // or an empty string.  All other TWAIN Local commands use POST,
            // and must specify a content type...
            if (a_blInfoInfoex)
            {
                // Certification only: we have no header...
                if (szXPrivetToken == null)
                {
                    return (null);
                }

                // Apps do this: send a privet token, the recommendation
                // for info/infoex is an empty string, indicated with two
                // double-quotes "".
                aszHeader = new string[] {
                    "X-Privet-Token: \"\""
                };
                return (aszHeader);
            }

            // Certification only: build the header, we're only allowing
            // this kind of flexibility to support the Certification tool.
            // Application writers shouldn't support this part of the code,
            // except to generate an error, if they've not first obtained
            // an X-Privet-Token...
            if (szXPrivetToken == null)
            {
                aszHeader = new string[] {
                    szContentType
                };
                return (aszHeader);
            }

            // Apps do this: for any POST commands...
            aszHeader = new string[] {
                szContentType,
                "X-Privet-Token: " + szXPrivetToken
            };
            return (aszHeader);
        }

        /// <summary>
        /// One stop shop for sending commands to the scanner.
        /// </summary>
        /// <param name="a_szReason">reason for the call, for logging</param>
        /// <param name="a_szUri">our target</param>
        /// <param name="a_szMethod">http method (ex: POST, DELETE...)</param>
        /// <param name="a_aszHeader">array of headers to send or null</param>
        /// <param name="a_szData">data to send or null</param>
        /// <param name="a_szUploadFile">upload data from a file</param>
        /// <param name="a_szOutputFile">redirect the data to a file</param>
        /// <param name="a_iTimeout">timeout in milliseconds</param>
        /// <param name="a_httpreplystyle">how we know when the command is complete</param>
        /// <returns>true on success</returns>
        private bool ClientHttpRequest
        (
            string a_szReason,
            ref ApiCmd a_apicmd,
            string a_szUri,
            string a_szMethod,
            string[] a_aszHeader,
            string a_szData,
            string a_szUploadFile,
            string a_szOutputFile,
            int a_iTimeout,
            ApiCmd.HttpReplyStyle a_httpreplystyle
        )
        {
            bool blSuccess;
            string szCode;

            // Our normal path...
            if (a_httpreplystyle != ApiCmd.HttpReplyStyle.Event)
            {
                // Send the RESTful API command...
                blSuccess = a_apicmd.HttpRequest
                (
                    a_szReason,
                    a_szUri,
                    a_szMethod,
                    a_aszHeader,
                    a_szData,
                    a_szUploadFile,
                    a_szOutputFile,
                    a_iTimeout,
                    a_httpreplystyle
                );
                if (!blSuccess)
                {
                    return (false);
                }

                // Try to get any session data that may be in the payload...
                blSuccess = ParseSession(a_szReason, a_apicmd, out szCode);
                if (!blSuccess)
                {
                    ClientReturnError(a_apicmd, false, szCode, -1, a_szReason + ": ParseSession failed - " + szCode);
                    return (false);
                }
            }

            // Handle events, we only expect to come down this path
            // once per session...
            else
            {
                // We already have a thread, so shut it down...
                if (m_waitforeventsinfo != null)
                {
                    m_waitforeventsinfo.Dispose();
                    m_waitforeventsinfo = null;
                }

                // Squirrel the information away for the new thread...
                m_waitforeventsinfo = new WaitForEventsInfo();
                m_waitforeventsinfo.m_apicmd = a_apicmd;
                m_waitforeventsinfo.m_threadCommunication = new Thread(new ParameterizedThreadStart(ClientScannerWaitForEventsCommunicationLaunchpad));
                m_waitforeventsinfo.m_threadProcessing = new Thread(new ParameterizedThreadStart(ClientScannerWaitForEventsProcessingLaunchpad));
                m_waitforeventsinfo.m_szReason = a_szReason;
                m_waitforeventsinfo.m_dnssddeviceinfo = a_apicmd.GetDnssdDeviceInfo();
                m_waitforeventsinfo.m_szUri = a_szUri;
                m_waitforeventsinfo.m_szMethod = a_szMethod;
                m_waitforeventsinfo.m_aszHeader = a_aszHeader;
                m_waitforeventsinfo.m_szData = a_szData;
                m_waitforeventsinfo.m_szUploadFile = a_szUploadFile;
                m_waitforeventsinfo.m_szOutputFile = a_szOutputFile;
                m_waitforeventsinfo.m_iTimeout = a_iTimeout;
                m_waitforeventsinfo.m_httpreplystyle = a_httpreplystyle;

                // Start the threads...
                m_waitforeventsinfo.m_threadProcessing.Start(this);
                m_waitforeventsinfo.m_threadCommunication.Start(this);
            }

            // All done...
            return (true);
        }

        /// <summary>
        /// Help with waiting for events (communication)...
        /// </summary>
        /// <param name="a_objectParameters">our object</param>
        private void ClientScannerWaitForEventsCommunicationLaunchpad
        (
            object a_objectParameters
        )
        {
            TwainLocalScanner twainlocalscanner;
            twainlocalscanner = (TwainLocalScanner)a_objectParameters;
            twainlocalscanner.ClientScannerWaitForEventsCommunicationHelper();
        }

        /// <summary>
        /// Help with waiting for events (communication)...
        /// </summary>
        private void ClientScannerWaitForEventsCommunicationHelper()
        {
            bool blSuccess;
            long lSessionRevision = 0;
            string szSessionState;
            ApiCmd apicmd = m_waitforeventsinfo.m_apicmd;

            // Loop until something stops us...
            for (;;)
            {
                string szClientCreateCommandId = "";
                string szSessionRevision = "0";
                SessionState sessionstate = SessionState.noSession;

                // Get data from our session...
                if (m_twainlocalsession != null)
                {
                    szClientCreateCommandId = m_twainlocalsession.ClientCreateCommandId();
                    szSessionRevision = m_twainlocalsession.GetSessionRevision().ToString();
                    sessionstate = m_twainlocalsession.GetSessionState();
                }

                // Update the data, first we need a new command id for this
                // instance of the long poll...
                string szData = m_waitforeventsinfo.m_szData;
                szData = szData.Replace("@@@COMMANDID@@@", szClientCreateCommandId);

                // Session data is protected...
                lock (m_waitforeventsinfo.m_objectlapicmdLock)
                {
                    // Report our current session id to the scanner, this
                    // will either be the revision from the session object
                    // or from the last event...
                    szData = szData.Replace("@@@SESSIONREVISION@@@", (lSessionRevision == 0) ? szSessionRevision : lSessionRevision.ToString());

                    // If we've gone to noSession, we should scoot, since
                    // it's no longer possible to receive events...
                    if ((m_twainlocalsession == null) || (sessionstate == SessionState.noSession))
                    {
                        // Initialize the object, and that's it...
                        apicmd.HttpRequest
                        (
                            m_waitforeventsinfo.m_szReason,
                            m_waitforeventsinfo.m_szUri,
                            m_waitforeventsinfo.m_szMethod,
                            m_waitforeventsinfo.m_aszHeader,
                            szData,
                            m_waitforeventsinfo.m_szUploadFile,
                            m_waitforeventsinfo.m_szOutputFile,
                            m_waitforeventsinfo.m_iTimeout,
                            m_waitforeventsinfo.m_httpreplystyle,
                            true
                        );
                        apicmd.DeviceResponseSetStatus
                        (
                            false,
                            "invalidSessionId",
                            -1,
                            "{" +
                            "\"kind\":\"twainlocalscanner\"," +
                            "\"commandId\":\"" + szClientCreateCommandId + "\"," +
                            "\"method\":\"waitForEvents\"," +
                            "\"results\":{" +
                            "\"success\":false," +
                            "\"code\":\"invalidSessionId\"" +
                            "}" + // results
                            "}", //root
                            200
                        );
                        apicmd.WaitForEventsCallback();
                        return;
                    }
                }

                // Send the RESTful API command...
                blSuccess = apicmd.HttpRequest
                (
                    m_waitforeventsinfo.m_szReason,
                    m_waitforeventsinfo.m_szUri,
                    m_waitforeventsinfo.m_szMethod,
                    m_waitforeventsinfo.m_aszHeader,
                    szData,
                    m_waitforeventsinfo.m_szUploadFile,
                    m_waitforeventsinfo.m_szOutputFile,
                    m_waitforeventsinfo.m_iTimeout,
                    m_waitforeventsinfo.m_httpreplystyle
                );

                // Handle errors...
                if (!blSuccess)
                {
                    switch (apicmd.HttpStatus())
                    {
                        // Ruh-roh...
                        default:
                            Log.Error("ClientScannerWaitForEventsHelper: bad status..." + apicmd.HttpStatus());
                            continue;

                        // Issue a new command...
                        case WebExceptionStatus.ReceiveFailure:
                        case WebExceptionStatus.Timeout:
                            continue;
                    }
                }

                // Send this off for processing, we have to lock when adding
                // it to the list...
                lock (m_waitforeventsinfo.m_objectlapicmdLock)
                {
                    m_waitforeventsinfo.m_lapicmdEvents.Add(apicmd);
                }

                // We need a new one of these for the next long poll, the
                // code in this function collects the session state and
                // the session revision...
                apicmd = new ApiCmd(apicmd, out szSessionState, out lSessionRevision);
                if (szSessionState == "noSession")
                {
                    return;
                }

                // Wake up the processor...
                m_autoreseteventWaitForEventsProcessing.Set();
            }
        }

        /// <summary>
        /// Help with waiting for events (processing)...
        /// </summary>
        /// <param name="a_objectParameters">our object</param>
        private void ClientScannerWaitForEventsProcessingLaunchpad
        (
            object a_objectParameters
        )
        {
            TwainLocalScanner twainlocalscanner;
            twainlocalscanner = (TwainLocalScanner)a_objectParameters;
            twainlocalscanner.ClientScannerWaitForEventsProcessingHelper();
        }

        /// <summary>
        /// Help with waiting for events (processing)...
        /// </summary>
        private void ClientScannerWaitForEventsProcessingHelper()
        {
            // Loop until something stops us...
            for (;;)
            {
                // Wait for the communication thread to give us work...
                if (!m_autoreseteventWaitForEventsProcessing.WaitOne())
                {
                    // We've been asked to scoot...
                    m_autoreseteventWaitForEvents.Set();
                    return;
                }

                // Loop for as long as we find data in the list...
                for (;;)
                {
                    ApiCmd apicmd;

                    // Pull the first item from the list, if the list is empty
                    // then drop out of this loop.  We have to protect this
                    // action, since the communication thread can add new content
                    // at any time...
                    lock (m_waitforeventsinfo.m_objectlapicmdLock)
                    {
                        // No more data...
                        if (m_waitforeventsinfo.m_lapicmdEvents.Count == 0)
                        {
                            break;
                        }

                        // Get the first item...
                        apicmd = m_waitforeventsinfo.m_lapicmdEvents[0];

                        // Delete the first item from the list...
                        m_waitforeventsinfo.m_lapicmdEvents.RemoveAt(0);
                    }

                    // Handle the event, at this point all we ever expect to see
                    // are updates for the session object...
                    lock (m_objectLock)
                    {
                        string szCode;

                        // Parse the data...
                        ParseSession(m_waitforeventsinfo.m_szReason, apicmd, out szCode);

                        // If we've gone to noSession, we should scoot, since
                        // it's no longer possible to receive events...
                        if (m_twainlocalsession.GetSessionState() == SessionState.noSession)
                        {
                            // Do the callback, if we have one...
                            apicmd.WaitForEventsCallback();

                            // Tell the communication thread to stop...
                            m_waitforeventsinfo.m_apicmd.HttpAbort();
                            break;
                        }
                    }

                    // Do the callback, if we have one...
                    apicmd.WaitForEventsCallback();
                }

                // Wake up anybody who might want to know that an event
                // has just gone by...
                m_autoreseteventWaitForEvents.Set();
            }
        }

        #endregion


        ///////////////////////////////////////////////////////////////////////////////
        // Private Device Methods...
        ///////////////////////////////////////////////////////////////////////////////
        #region Private Device Methods...

        /// <summary>
        /// Create an X-Privet-Token, we do this to generate a brand new value,
        /// and we do it to recreate a value that we want to validate...
        /// </summary>
        /// <param name="a_lTicks">0 to generate a new one, or the ticks from a previously created token</param>
        /// <returns>the token</returns>
        public string CreateXPrivetToken(long a_lTicks = 0)
        {
            long lTicks;
            string szXPrivetToken;

            // Use our ticks, this is for validation...
            if (a_lTicks > 0)
            {
                lTicks = a_lTicks;
            }

            // Otherwise use the clock, this is for generation...
            else
            {
                lTicks = DateTime.Now.Ticks;
            }

            // This is what's recommended...
            // XSRF_token = base64( SHA1(device_secret + DELIMITER + issue_timecounter) + DELIMITER + issue_timecounter )      
            szXPrivetToken = m_szDeviceSecret + ":" + lTicks;
            using (SHA1Managed sha1managed = new SHA1Managed())
            {
                byte[] abHash = sha1managed.ComputeHash(Encoding.UTF8.GetBytes(szXPrivetToken));
                szXPrivetToken = Convert.ToBase64String(abHash);
            }
            szXPrivetToken += ":" + lTicks;

            // All done...
            return (szXPrivetToken);
        }

        /// <summary>
        /// Return error information from a device function...
        /// </summary>
        /// <param name="a_szReason">our caller</param>
        /// <param name="a_apicmd">info about the command</param>
        /// <param name="a_szCode">the status code</param>
        /// <param name="a_szJsonKey">json key to point of error or null</param>
        /// <param name="a_lResponseCharacterOffset">character offset of json error or -1</param>
        /// <returns>true on success</returns>
        private bool DeviceReturnError(string a_szReason, ApiCmd a_apicmd, string a_szCode, string a_szJsonKey, long a_lResponseCharacterOffset)
        {
            string szResponse;

            // Log it...
            Log.Error
            (
                a_szReason + ": error code=" + a_szCode +
                (!string.IsNullOrEmpty(a_szJsonKey) ? " key=" + a_szJsonKey : "") +
                ((a_lResponseCharacterOffset >= 0) ? " offset=" + a_lResponseCharacterOffset : "")
            );

            // If we don't have an ApiCmd to respond to, we're done...
            if (a_apicmd == null)
            {
                return (true);
            }

            // Handle a JSON error...
            if (string.IsNullOrEmpty(a_szCode) || (a_szCode == "invalidJson"))
            {
                // Our base response...
                szResponse =
                    "{" +
                    "\"kind\":\"twainlocalscanner\"," +
                    "\"commandId\":\"" + a_apicmd.GetCommandId() + "\"," +
                    "\"method\":\"" + a_apicmd.GetCommandName() + "\"," +
                    "\"results\":{" +
                    "\"success\":false," +
                    "\"code\":\"" + "invalidJson" + "\"," +
                    "\"characterOffset\":" + a_lResponseCharacterOffset +
                    "}" + // results
                    "}"; //root
            }

            // If it's an invalidTask, then include that data...
            else if (a_szCode == "invalidTask")
            {
                szResponse =
                    "{" +
                    "\"kind\":\"twainlocalscanner\"," +
                    "\"commandId\":\"" + a_apicmd.GetCommandId() + "\"," +
                    "\"method\":\"" + a_apicmd.GetCommandName() + "\"," +
                    "\"results\":{" +
                    "\"success\":true," +
                    "\"session\":{" +
                    "\"sessionId\":\"" + m_twainlocalsession.GetSessionId() + "\"," +
                    "\"revision\":\"" + m_twainlocalsession.GetSessionRevision() + "\"," +
                    "\"state\":\"" + m_twainlocalsession.GetSessionState() + "\"," +
                    "\"status\":{" +
                    "\"success\":" + (m_twainlocalsession.GetSessionStatusSuccess() ? "true" : "false") + "," +
                    "\"detected\":\"" + m_twainlocalsession.GetSessionStatusDetected() + "\"" +
                    "}," + // status
                    "\"task\":" + a_szJsonKey + 
                    "}" + // session
                    "}" + // results
                    "}"; //root
            }

            // Anything else...
            else
            {
                // Our base response...
                szResponse =
                    "{" +
                    "\"kind\":\"twainlocalscanner\"," +
                    "\"commandId\":\"" + a_apicmd.GetCommandId() + "\"," +
                    "\"method\":\"" + a_apicmd.GetCommandName() + "\"," +
                    "\"results\":{" +
                    "\"success\":false," +
                    "\"code\":\"" + a_szCode + "\"" +
                    "}" + // results
                    "}"; //root
            }

            // Send the response...
            a_apicmd.HttpRespond(a_szCode, szResponse);

            // All done...
            return (true);
        }

        /// <summary>
        /// Queue an event for waitForEvents to send...
        /// </summary>
        /// <param name="a_szEvent">the event to send</param>
        /// <param name="a_sessionstate">the state to send</param>
        private void DeviceSendEvent(string a_szEvent, SessionState a_sessionstate)
        {
            // Guard us...
            lock (m_objectLock)
            {
                // We only have an event if we have a session...
                if (m_twainlocalsession != null)
                {
                    ApiCmd apicmd = new ApiCmd(null);
                    m_twainlocalsession.SetSessionRevision(m_twainlocalsession.GetSessionRevision() + 1);
                    apicmd.SetEvent(a_szEvent, a_sessionstate.ToString(), m_twainlocalsession.GetSessionRevision());
                    DeviceUpdateSession("DeviceSendEvent", m_apicmdEvent, true, apicmd, a_sessionstate, m_twainlocalsession.GetSessionRevision(), a_szEvent);
                }
            }
        }

        /// <summary>
        /// Our device session timeout callback...
        /// </summary>
        /// <param name="a_objectState"></param>
        internal void DeviceSessionTimerCallback(object a_objectState)
        {
            // Our scanner object...
            TwainLocalScanner twainlocalscanner = (TwainLocalScanner)a_objectState;

            // Send an event to let the app know that it's tooooooo late...
            twainlocalscanner.DeviceSendEvent("sessionTimedOut", SessionState.noSession);

            // Make a note of what we're doing...
            Log.Error("DeviceSessionTimerCallback: session timeout...");

            // Give the system two seconds to deliver the message, otherwise
            // what will happen is the client will see that it's lost
            // communication.  Which it should interpret as the loss of the
            // session.  This is just a nicer way of getting there...
            Thread.Sleep(2000);

            // Scrag the session...
            twainlocalscanner.SetSessionState(SessionState.noSession, "Session timeout...");
        }

        /// <summary>
        /// Refresh our session timer...
        /// </summary>
        public void DeviceSessionRefreshTimer()
        {
            m_timerSession.Change(Timeout.Infinite, Timeout.Infinite);
            m_timerSession.Change(m_lSessionTimeout, Timeout.Infinite);
        }

        /// <summary>
        /// Try to shutdown TWAIN Direct on TWAIN...
        /// </summary>
        /// <param name="a_blForce">force the shutdown</param>
        private void DeviceShutdownTwainDirectOnTwain(bool a_blForce)
        {
            // Apparently we've already done this...
            if (m_twainlocalsession == null)
            {
                return;
            }

            //
            // We'll only fully shutdown if we have no outstanding
            // images, so the close reply should tell us that, then
            // we can issue and exit to shut it down.  If we know
            // that the session is closed, then the releaseImageBlocks
            // function is the one that'll do the final shutdown when
            // the last block is released...
            if (!a_blForce && (m_twainlocalsession != null) && (m_twainlocalsession.GetSessionState() != SessionState.noSession))
            {
                return;
            }

            // Shut down the process...
            if (m_twainlocalsession.GetIpcTwainDirectOnTwain() != null)
            {
                m_twainlocalsession.GetIpcTwainDirectOnTwain().Dispose();
                m_twainlocalsession.SetIpcTwainDirectOnTwain(null);
            }

            // Make sure the process is gone...
            if (m_twainlocalsession.GetProcessTwainDirectOnTwain() != null)
            {
                // Log what we're doing...
                Log.Info("kill>>> " + m_twainlocalsession.GetProcessTwainDirectOnTwain().StartInfo.FileName);
                Log.Info("kill>>> " + m_twainlocalsession.GetProcessTwainDirectOnTwain().StartInfo.Arguments);

                // Wait a bit for it...
                if (!m_twainlocalsession.GetProcessTwainDirectOnTwain().WaitForExit(5000))
                {
                    m_twainlocalsession.GetProcessTwainDirectOnTwain().Kill();
                }
                m_twainlocalsession.SetProcessTwainDirectOnTwain(null);
            }
        }

        /// <summary>
        /// Update the session object...
        /// </summary>
        /// <param name="a_szReason">something for logging</param>
        /// <param name="a_apicmd">the command object we're working on</param>
        /// <param name="a_apicmdEvent">the command object with event data</param>
        /// <param name="a_szSessionState">the state of the Scanner API session</param>
        /// <param name="a_lSessionRevision">the current session revision</param>
        /// <param name="a_szEventName">name of an event (or null)</param>
        /// <returns>true on success</returns>
        private bool DeviceUpdateSession
        (
            string a_szReason,
            ApiCmd a_apicmd,
            bool a_blWaitForEvents,
            ApiCmd a_apicmdEvent,
            SessionState a_esessionstate,
            long a_lSessionRevision,
            string a_szEventName
        )
        {
            long ii;
            string szResponse;
            string szSessionObjects;
            string szEventsArray = "";
            ApiCmd apicmd;

            //////////////////////////////////////////////////
            // We're responding to the /privet/info or the
            // /privet/infoex command...
            #region We're responding to the /privet/info command...
            if ((a_apicmd != null) && ((a_apicmd.GetUri() == "/privet/info") ||  (a_apicmd.GetUri() == "/privet/infoex")))
            {
                string szDeviceState;
                string szManufacturer;
                string szModel;
                string szSerialNumber;
                string szFirmware;
                long longUptime;
                Dnssd.DnssdDeviceInfo dnssddeviceinfo = GetDnssdDeviceInfo();

                // Our uptime is from when the process started...
                longUptime = (long)(DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds;

                // Device state...
                if (m_twainlocalsession == null)
                {
                    szDeviceState = "idle";
                    szManufacturer = m_twainlocalsessionInfo.DeviceRegisterGetTwainLocalManufacturer();
                    szModel = m_twainlocalsessionInfo.DeviceRegisterGetTwainLocalProductName();
                    szSerialNumber = m_twainlocalsessionInfo.DeviceRegisterGetTwainLocalSerialNumber();
                    szFirmware = m_twainlocalsessionInfo.DeviceRegisterGetTwainLocalVersion();
                }
                else
                {
                    // The user has been warned in the Privet docs not to rely
                    // on this information.  However, it does have its uses, so
                    // we'll check it out during certification...
                    switch (m_twainlocalsession.GetSessionState())
                    {
                        default: szDeviceState = "stopped"; break;
                        case SessionState.noSession: szDeviceState = "idle"; break;
                        case SessionState.capturing: szDeviceState = "processing"; break;
                        case SessionState.closed: szDeviceState = "processing"; break;
                        case SessionState.draining: szDeviceState = "processing"; break;
                        case SessionState.ready: szDeviceState = "processing"; break;
                    }

                    // This is the best we can do for this info...
                    szManufacturer = m_twainlocalsessionInfo.DeviceRegisterGetTwainLocalManufacturer();
                    szModel = m_twainlocalsessionInfo.DeviceRegisterGetTwainLocalProductName();
                    szSerialNumber = m_twainlocalsessionInfo.DeviceRegisterGetTwainLocalSerialNumber();
                    szFirmware = m_twainlocalsessionInfo.DeviceRegisterGetTwainLocalVersion();

                    // Protection...
                    if (string.IsNullOrEmpty(szManufacturer))
                    {
                        szManufacturer = "(no manufacturer)";
                    }
                    if (string.IsNullOrEmpty(szModel))
                    {
                        szModel = "(no model)";
                    }
                    if (string.IsNullOrEmpty(szSerialNumber))
                    {
                        szSerialNumber = "(no serial number)";
                    }
                }

                // Add additional data for infoex...
                string szInfoex = "";
                if (a_apicmd.GetUri() == "/privet/infoex")
                {
                    szInfoex =
                        "," +
                        "\"clouds\":[" +
                        "]";
                }

                // Construct a response, always make a new X-Privet-Token...
                szResponse =
                    "{" +
                    "\"version\":\"1.0\"," +
                    "\"name\":\"" + dnssddeviceinfo.GetTxtTy() + "\"," +
                    "\"description\":\"" + dnssddeviceinfo.GetTxtNote() + "\"," +
                    "\"url\":\"\"," +
                    "\"type\":\"" + dnssddeviceinfo.GetTxtType() + "\"," +
                    "\"id\":\"\"," +
                    "\"device_state\":\"" + szDeviceState + "\"," +
                    "\"connection_state\":\"offline\"," +
                    "\"manufacturer\":\"" + szManufacturer + "\"," +
                    "\"model\":\"" + szModel + "\"," +
                    "\"serial_number\":\"" + szSerialNumber + "\"," +
                    "\"firmware\":\"" + szFirmware + "\"," +
                    "\"uptime\":\"" + longUptime + "\"," +
                    "\"setup_url\":\"" + "" + "\"," +
                    "\"support_url\":\"" + "" + "\"," +
                    "\"update_url\":\"" + "" + "\"," +
                    "\"x-privet-token\":\"" + CreateXPrivetToken(0) + "\"," +
                    "\"api\":[" +
                    "\"/privet/twaindirect/session\"" +
                    "]," +
                    "\"semantic_state\":\"" + "" + "\"" +
                    szInfoex +
                    "}";

                // Send the response...
                a_apicmd.HttpRespond("success", szResponse);

                // All done...
                return (true);
            }
            #endregion

            ////////////////////////////////////////////////////////////////
            // /privet/twaindirect/session command
            #region /privet/twaindirect/session command
            if (    (a_apicmd != null)
                &&  (a_apicmd.GetUri() == "/privet/twaindirect/session")
                &&  !a_blWaitForEvents)
            {
                // Okay, you're going to love this.  So in order to change our revision
                // number in a meaningful way, we'll generate the string data we want
                // to send back and compare it to the previous string we generated, if
                // there is a difference, then we'll update the revision.  Of course we
                // only do this if we have an active session.
                //
                // The chief benefit of doing it this way, is that it's centralized and
                // easy to understand.  The chief drawback is that it feels groadie with
                // the if-statements...

                // Update the session's status, but only once.  Put another way, after
                // startCapturing is called, we'll record one boo-boo from a RESTful,
                // command, and skip any others, until the next startCapturing is called...
                if (m_twainlocalsession.GetSessionStatusSuccess() && !a_apicmd.GetSessionStatusSuccess())
                {
                    m_twainlocalsession.SetSessionStatusSuccess(a_apicmd.GetSessionStatusSuccess());
                    m_twainlocalsession.SetSessionStatusDetected(a_apicmd.GetSessionStatusDetected());
                }

                // Start building the session object...
                szSessionObjects =
                    "\"session\":{" +
                    "\"sessionId\":\"" + m_twainlocalsession.GetSessionId() + "\"," +
                    "\"revision\":" + m_twainlocalsession.GetSessionRevision() + "," +
                    "\"state\":\"" + a_esessionstate.ToString() + "\"," +
                    "\"status\":{" +
                    "\"success\":" + (m_twainlocalsession.GetSessionStatusSuccess() ? "true" : "false") + "," +
                    "\"detected\":\"" + m_twainlocalsession.GetSessionStatusDetected() + "\"" +
                    "}," + // status
                    a_apicmd.GetImageBlocksJson(a_esessionstate.ToString());

                // Add the TWAIN Direct options, if any...
                string szTaskReply = a_apicmd.GetTaskReply();
                if (!string.IsNullOrEmpty(szTaskReply))
                {
                    szSessionObjects += "\"task\":" + szTaskReply + ",";
                }

                // End the session object...
                if (szSessionObjects.EndsWith(","))
                {
                    szSessionObjects = szSessionObjects.Substring(0, szSessionObjects.Length - 1);
                }
                szSessionObjects+= "}";

                // Check to see if we have to update our revision number...
                if (    string.IsNullOrEmpty(m_twainlocalsession.GetSessionSnapshot())
                    ||  (szSessionObjects != m_twainlocalsession.GetSessionSnapshot()))
                {
                    szSessionObjects = szSessionObjects.Replace
                    (
                        "\"revision\":" + m_twainlocalsession.GetSessionRevision() + ",",
                        "\"revision\":" + (m_twainlocalsession.GetSessionRevision() + 1) + ","
                    );
                    m_twainlocalsession.SetSessionRevision(m_twainlocalsession.GetSessionRevision() + 1);
                    m_twainlocalsession.SetSessionSnapshot(szSessionObjects);
                }

                // Construct a response...
                szResponse =
                    "{" +
                    "\"kind\":\"twainlocalscanner\"," +
                    "\"commandId\":\"" + a_apicmd.GetCommandId() + "\"," +
                    "\"method\":\"" + a_apicmd.GetCommandName() + "\"," +
                    "\"results\":{" +
                    "\"success\":true," +
                    a_apicmd.GetMetadata() +
                    szSessionObjects +
                    "}" + // results
                    "}";  // root

                // Send the response, note that any multipart contruction work
                // takes place in this function...
                a_apicmd.HttpRespond("success", szResponse);

                // Okay, now do the state transition...
                SetSessionState(a_esessionstate);

                // All done...
                return (true);
            }
            #endregion

            ////////////////////////////////////////////////////////////////
            // /privet/twaindirect/session event
            #region /privet/twaindirect/session event
            if (    ((a_apicmd == null) || (a_apicmd.GetUri() == "/privet/twaindirect/session"))
                &&  a_blWaitForEvents)
            {
                // This should never happen, but let's be sure...
                if (a_esessionstate == SessionState.noSession)
                {
                    return (true);
                }

                // Do we have new event data?
                if (a_apicmdEvent != null)
                {
                    for (ii = 0; ii < m_twainlocalsession.GetApicmdEvents().Length; ii++)
                    {
                        if (m_twainlocalsession.GetApicmdEvents()[ii] == null)
                        {
                            a_apicmdEvent.SetEvent(a_szEventName, a_esessionstate.ToString(), a_lSessionRevision);
                            m_twainlocalsession.SetApicmdEvent(ii, a_apicmdEvent);
                            break;
                        }
                    }
                }

                // Expire any events that are too old, or which have a
                // revision number less than or equal to the current
                // revision number from the last waitForEvents command.
                for (ii = 0; ii < m_twainlocalsession.GetApicmdEvents().Length; ii++)
                {
                    // Grab our apicmd...
                    apicmd = m_twainlocalsession.GetApicmdEvents()[ii];

                    // All done...
                    if (apicmd == null)
                    {
                        break;
                    }

                    // Is this older than the last revision sent to us in
                    // a waitForEvents call?  If so, discard it.
                    if (apicmd.DiscardEvent(m_twainlocalsession.GetWaitForEventsSessionRevision()))
                    {
                        // Delete the item by shifting the rest of the array over it...
                        for (long jj = ii; jj < (m_twainlocalsession.GetApicmdEvents().Length - 1); jj++)
                        {
                            m_twainlocalsession.SetApicmdEvent(jj, m_twainlocalsession.GetApicmdEvents()[jj+1]);
                        }
                        m_twainlocalsession.SetApicmdEvent(m_twainlocalsession.GetApicmdEvents().Length - 1, null);
                    }
                }

                // Sort whatever is left, so that we give it to the caller
                // in order of increasing revision numbers.  Remove any
                // duplicates.

                // Generate the event array to send to the caller,
                // if we have a place to send it.  The data is already
                // filtered and sorted, so we send all of it.
                if (a_apicmd != null)
                {
                    // We have no events...
                    if (m_twainlocalsession.GetApicmdEvents()[0] == null)
                    {
                        return (true);
                    }

                    // Start the array...
                    szEventsArray = "\"events\":[";

                    // Add each event object...
                    szSessionObjects = "";
                    for (ii = 0; ii < m_twainlocalsession.GetApicmdEvents().Length; ii++)
                    {
                        // Grab our apicmd...
                        apicmd = m_twainlocalsession.GetApicmdEvents()[ii];

                        // We're done...
                        if (apicmd == null)
                        {
                            break;
                        }

                        // Update the session, if needed...
                        if (m_twainlocalsession.GetSessionStatusSuccess() && !apicmd.GetSessionStatusSuccess())
                        {
                            m_twainlocalsession.SetSessionStatusSuccess(apicmd.GetSessionStatusSuccess());
                            m_twainlocalsession.SetSessionStatusDetected(apicmd.GetSessionStatusDetected());
                        }

                        // We're adding to existing stuff...
                        if (!string.IsNullOrEmpty(szSessionObjects))
                        {
                            szSessionObjects += ",";
                        }

                        // Build this event...
                        szSessionObjects +=
                            "{" +
                            "\"event\":\"" + apicmd.GetEventName() + "\"," +
                            "\"session\":{" +
                            "\"sessionId\":\"" + m_twainlocalsession.GetSessionId() + "\"," +
                            "\"revision\":" + apicmd.GetSessionRevision() + "," +
                            "\"state\":\"" + apicmd.GetSessionState() + "\"," +
                            "\"status\":{" +
                            "\"success\":" + (m_twainlocalsession.GetSessionStatusSuccess() ? "true" : "false") + "," +
                            "\"detected\":\"" + m_twainlocalsession.GetSessionStatusDetected() + "\"" +
                            "}," + // status
                            apicmd.GetImageBlocksJson(apicmd.GetSessionState());
                        if (szSessionObjects.EndsWith(","))
                        {
                            szSessionObjects = szSessionObjects.Substring(0, szSessionObjects.Length - 1);
                        }
                        szSessionObjects += "}";
                        szSessionObjects += "}";
                    }

                    // Add the events...
                    szEventsArray += szSessionObjects;

                    // End the array...
                    szEventsArray += "]";

                    // Construct a response...
                    szResponse =
                        "{" +
                        "\"kind\":\"twainlocalscanner\"," +
                        "\"commandId\":\"" + a_apicmd.GetCommandId() + "\"," +
                        "\"method\":\"waitForEvents\"," +
                        "\"results\":{" +
                        "\"success\":true," +
                        szEventsArray +
                        "}" + // results
                        "}";  // root

                    // Send the response, note that any multipart contruction work
                    // takes place in this function...
                    a_apicmd.HttpRespond("success", szResponse);

                    // All done...
                    return (true);
                }
            }
            #endregion

            // Getting this far is a bad thing.  We shouldn't be here
            // unless somebody upstream fell asleep at the switch...
            Log.Error("UpdateSession: bad uri..." + ((a_apicmd != null) ? a_apicmd.GetUri() : "no apicmd"));
            return (false);
        }

        /// <summary>
        /// return info about the device...
        /// </summary>
        /// <param name="a_apicmd">the info command the caller sent</param>
        /// <returns>true on success</returns>
        private bool DeviceInfo(ref ApiCmd a_apicmd)
        {
            bool blSuccess;
            string szFunction = "DeviceInfo";

            // Reply to the command with a session object...
            blSuccess = DeviceUpdateSession(szFunction, a_apicmd, false, null, m_twainlocalsessionInfo.GetSessionState(), -1, null);
            if (!blSuccess)
            {
                DeviceReturnError(szFunction, a_apicmd, "critical", null, -1);
                return (false);
            }

            // All done...
            return (true);
        }

        /// <summary>
        /// Close a scanning session...
        /// </summary>
        /// <param name="a_apicmd">the close command the caller sent</param>
        /// <returns>true on success</returns>
        private bool DeviceScannerCloseSession(ref ApiCmd a_apicmd)
        {
            bool blSuccess;
            long lResponseCharacterOffset;
            string szIpc;
            SessionState sessionstate;
            string szFunction = "DeviceScannerCloseSession";

            // Protect our stuff...
            lock (m_objectLock)
            {
                // Refresh our timer...
                DeviceSessionRefreshTimer();

                // State check...
                switch (m_twainlocalsession.GetSessionState())
                {
                    // These are okay...
                    case SessionState.ready:
                    case SessionState.capturing:
                    case SessionState.draining:
                        break;

                    // These are not...
                    case SessionState.noSession:
                    case SessionState.closed:
                    default:
                        DeviceReturnError(szFunction, a_apicmd, "invalidState", null, -1);
                        return (false);
                }

                // Validate...
                if (    (m_twainlocalsession == null)
                    ||  (m_twainlocalsession.GetIpcTwainDirectOnTwain() == null))
                {
                    DeviceReturnError(szFunction, a_apicmd, "invalidSessionId", null, -1);
                    return (false);
                }

                // Close the scanner...
                m_twainlocalsession.GetIpcTwainDirectOnTwain().Write
                (
                    "{" +
                    "\"method\":\"closeSession\"" +
                    "}"
                );

                // Get the result...
                JsonLookup jsonlookup = new JsonLookup();
                szIpc = m_twainlocalsession.GetIpcTwainDirectOnTwain().Read();
                if (!jsonlookup.Load(szIpc, out lResponseCharacterOffset))
                {
                    DeviceReturnError(szFunction, a_apicmd, "invalidJson", null, lResponseCharacterOffset);
                    return (false);
                }

                // Update the ApiCmd command object...
                switch (m_twainlocalsession.GetSessionState())
                {
                    default:
                        a_apicmd.UpdateUsingIpcData(jsonlookup, false, m_szImagesFolder);
                        break;
                    case SessionState.capturing:
                    case SessionState.draining:
                        a_apicmd.UpdateUsingIpcData(jsonlookup, true, m_szImagesFolder);
                        break;
                }

                // Parse it...
                if (!string.IsNullOrEmpty(a_apicmd.HttpResponseData()))
                {
                    blSuccess = jsonlookup.Load(a_apicmd.HttpResponseData(), out lResponseCharacterOffset);
                    if (!blSuccess)
                    {
                        Log.Error(szFunction + ": error parsing the reply (but we're going to continue)...");
                        // keep going, we can't lock the user into this state
                    }
                }

                // Exit the process...
                m_twainlocalsession.GetIpcTwainDirectOnTwain().Write
                (
                    "{" +
                    "\"method\":\"exit\"" +
                    "}"
                );

                // Figure out the session state we want to transition to.  If
                // we've lost our session, or we're ready, or we have no more
                // images to deliver, then transition to noSession...
                if (    (m_twainlocalsession == null)
                    ||  (m_twainlocalsession.GetSessionState() == SessionState.ready)
                    ||  m_twainlocalsession.GetSessionImageBlocksDrained())
                {
                    sessionstate = SessionState.noSession;
                }
                else
                {
                    sessionstate = SessionState.closed;
                }

                // Reply to the command with a session object...
                blSuccess = DeviceUpdateSession(szFunction, a_apicmd, false, null, sessionstate, -1, null);
                if (!blSuccess)
                {
                    DeviceReturnError(szFunction, a_apicmd, "critical", null, -1);
                    return (false);
                }

                // Shutdown TWAIN Direct on TWAIN, but only if we've run out of
                // images...
                DeviceShutdownTwainDirectOnTwain(false);
            }

            // All done...
            return (true);
        }

        /// <summary>
        /// Create a new scanning session...
        /// </summary>
        /// <param name="a_apicmd">the command the caller sent</param>
        /// <param name="a_szXPrivetToken">the X-Privet-Token for this session</param>
        /// <returns>true on success</returns>
        private bool DeviceScannerCreateSession(ref ApiCmd a_apicmd, string a_szXPrivetToken)
        {
            bool blSuccess;
            long lErrorErrorIndex;
            string szIpc;
            string szArguments;
            string szTwainDirectOnTwain;
            string szFunction = "DeviceScannerCreateSession";

            // Protect our stuff...
            lock (m_objectLock)
            {
                // Create it if we need it...
                if (m_twainlocalsession == null)
                {
                    m_twainlocalsession = new TwainLocalSession(a_szXPrivetToken);
                    m_twainlocalsession.DeviceRegisterLoad(this, Path.Combine(m_szWriteFolder, "register.txt"));
                }

                // Init stuff...
                szTwainDirectOnTwain = Config.Get("executablePath", "");
                szTwainDirectOnTwain = szTwainDirectOnTwain.Replace("TwainDirect.Scanner", "TwainDirect.OnTwain");

                // State check...
                if (m_twainlocalsession.GetSessionState() != SessionState.noSession)
                {
                    // We're running a session, and this is our current caller...
                    if (a_apicmd.HttpGetCallersHostName() == m_twainlocalsession.GetCallersHostName())
                    {
                        DeviceReturnError(szFunction, a_apicmd, "invalidState", null, -1);
                        return (false);
                    }

                    // Otherwise somebody else is trying to talk to us, and we
                    // need to tell them we're busy right now...
                    else
                    {
                        DeviceReturnError(szFunction, a_apicmd, "busy", null, -1);
                        return (false);
                    }
                }

                // Create an IPC...
                if (m_twainlocalsession.GetIpcTwainDirectOnTwain() == null)
                {
                    m_twainlocalsession.SetIpcTwainDirectOnTwain(new Ipc("socket|" + IPAddress.Loopback.ToString() + "|0", true));
                }

                // Arguments to the progream...
                szArguments = "ipc=\"" + m_twainlocalsession.GetIpcTwainDirectOnTwain().GetConnectionInfo() + "\"";
                szArguments += " images=\"" + m_szImagesFolder + "\"";
                szArguments += " twainlist=\"" + Path.Combine(m_szWriteFolder,"twainlist.txt") + "\"";

                // Get ready to start the child process...
                m_twainlocalsession.SetProcessTwainDirectOnTwain(new Process());
                m_twainlocalsession.GetProcessTwainDirectOnTwain().StartInfo.UseShellExecute = false;
                m_twainlocalsession.GetProcessTwainDirectOnTwain().StartInfo.WorkingDirectory = Path.GetDirectoryName(szTwainDirectOnTwain);
                m_twainlocalsession.GetProcessTwainDirectOnTwain().StartInfo.CreateNoWindow = true;
                m_twainlocalsession.GetProcessTwainDirectOnTwain().StartInfo.RedirectStandardOutput = false;
                if (TwainLocalScanner.GetPlatform() == Platform.WINDOWS)
                {
                    m_twainlocalsession.GetProcessTwainDirectOnTwain().StartInfo.FileName = szTwainDirectOnTwain;
                    m_twainlocalsession.GetProcessTwainDirectOnTwain().StartInfo.Arguments = szArguments;
                }
                else
                {
                    m_twainlocalsession.GetProcessTwainDirectOnTwain().StartInfo.FileName = "/usr/bin/mono";
                    m_twainlocalsession.GetProcessTwainDirectOnTwain().StartInfo.Arguments = "\"" + szTwainDirectOnTwain + "\"" + " " + szArguments;
                }
                m_twainlocalsession.GetProcessTwainDirectOnTwain().StartInfo.WindowStyle = ProcessWindowStyle.Hidden;

                // Log what we're doing...
                Log.Info("run>>> " + m_twainlocalsession.GetProcessTwainDirectOnTwain().StartInfo.FileName);
                Log.Info("run>>> " + m_twainlocalsession.GetProcessTwainDirectOnTwain().StartInfo.Arguments);

                // Start the child process.
                m_twainlocalsession.GetProcessTwainDirectOnTwain().Start();

                // Monitor our new process...
                m_twainlocalsession.GetIpcTwainDirectOnTwain().MonitorPid(m_twainlocalsession.GetProcessTwainDirectOnTwain().Id);
                m_twainlocalsession.GetIpcTwainDirectOnTwain().Accept();

                // Open the scanner...
                string szCommand =
                    "{" +
                    "\"method\":\"createSession\"," +
                    "\"scanner\":" + m_twainlocalsession.DeviceRegisterGetTwainLocalScanner() +
                    "}";
                m_twainlocalsession.GetIpcTwainDirectOnTwain().Write(szCommand);

                // Get the result...
                JsonLookup jsonlookup = new JsonLookup();
                szIpc = m_twainlocalsession.GetIpcTwainDirectOnTwain().Read();
                blSuccess = jsonlookup.Load(szIpc, out lErrorErrorIndex);
                if (!blSuccess)
                {
                    // Exit the process...
                    m_twainlocalsession.GetIpcTwainDirectOnTwain().Write
                    (
                        "{" +
                        "\"method\":\"exit\"" +
                        "}"
                    );
                    m_twainlocalsession.GetProcessTwainDirectOnTwain().WaitForExit(5000);
                    m_twainlocalsession.GetProcessTwainDirectOnTwain().Close();
                    m_twainlocalsession.SetProcessTwainDirectOnTwain(null);
                    DeviceReturnError(szFunction, a_apicmd, "invalidJson", null, lErrorErrorIndex);
                    return (false);
                }

                // Handle errors...
                if (jsonlookup.Get("status") != "success")
                {
                    // Exit the process...
                    m_twainlocalsession.GetIpcTwainDirectOnTwain().Write
                    (
                        "{" +
                        "\"method\":\"exit\"" +
                        "}"
                    );
                    m_twainlocalsession.GetProcessTwainDirectOnTwain().WaitForExit(5000);
                    m_twainlocalsession.GetProcessTwainDirectOnTwain().Close();
                    m_twainlocalsession.SetProcessTwainDirectOnTwain(null);
                    DeviceReturnError(szFunction, a_apicmd, jsonlookup.Get("status"), null, -1);
                    return (false);
                }

                // Update the ApiCmd command object...
                a_apicmd.UpdateUsingIpcData(jsonlookup, false, m_szImagesFolder);

                // Reply to the command with a session object, this is where we create our
                // session id, public session id and set the revision to 0...
                m_twainlocalsession.SetCallersHostName(a_apicmd.HttpGetCallersHostName());
                m_twainlocalsession.SetSessionId(Guid.NewGuid().ToString());
                m_twainlocalsession.ResetSessionRevision();
                blSuccess = DeviceUpdateSession(szFunction, a_apicmd, false, null, SessionState.ready, -1, null);
                if (!blSuccess)
                {
                    DeviceReturnError(szFunction, a_apicmd, "critical", null, -1);
                    return (false);
                }

                // Refresh our timer...
                DeviceSessionRefreshTimer();

                // Display what happened...
                Display("");
                Display("Session started by <" + a_apicmd.HttpGetCallersHostName() + ">");
            }

            // All done...
            return (true);
        }

        /// <summary>
        /// Get the current info on a scanning session.  This can happen in one
        /// of two ways, either as a standalone call to getSession, or as a part
        /// of waiting for events with waitForEvents.
        ///
        /// In the latter case we check to see if we have ApiCmd data, if we
        /// don't, we squirrel the event away.  If we do, we drain all of the
        /// events we currently have that aren't older than a certain number
        /// of seconds, and with revision numbers newer than the one received
        /// from the last call to waitForEvents.
        /// 
        /// An explicit call to getSession will reset the session timer.  A
        /// call to waitForEvents will not.
        /// </summary>
        /// <param name="a_apicmd">our command object</param>
        /// <param name="a_blSendEvents">send as events</param>
        /// <param name="a_blGetSession">get session for an event</param>
        /// <param name="a_blGetSession">get session for an event</param>
        /// <param name="a_szEventName">name of the event (or null)</param>
        /// <returns>true on success</returns>
        private bool DeviceScannerGetSession(ref ApiCmd a_apicmd, bool a_blSendEvents, bool a_blGetSession, string a_szEventName)
        {
            bool blSuccess;
            long lResponseCharacterOffset;
            string szIpc;
            ApiCmd apicmdEvent;
            string szFunction = "DeviceScannerGetSession";

            // Protect our stuff...
            lock (m_objectLock)
            {
                //////////////////////////////////////////////////////////////////////
                // This path is taken for getSession
                //////////////////////////////////////////////////////////////////////
                #region getSession

                // Handle getSession...
                if (!a_blSendEvents)
                {
                    // Refresh our timer...
                    DeviceSessionRefreshTimer();

                    // State check...
                    if (m_twainlocalsession.GetSessionState() == SessionState.noSession)
                    {
                        DeviceReturnError(szFunction, a_apicmd, "invalidState", null, -1);
                        return (false);
                    }

                    // Validate...
                    if (m_twainlocalsession.GetIpcTwainDirectOnTwain() == null)
                    {
                        DeviceReturnError(szFunction, a_apicmd, "invalidSessionId", null, -1);
                        return (false);
                    }

                    // Get the current session info...
                    m_twainlocalsession.GetIpcTwainDirectOnTwain().Write
                    (
                        "{" +
                        "\"method\":\"getSession\"" +
                        "}"
                    );

                    // Get the result...
                    JsonLookup jsonlookup = new JsonLookup();
                    szIpc = m_twainlocalsession.GetIpcTwainDirectOnTwain().Read();
                    if (!jsonlookup.Load(szIpc, out lResponseCharacterOffset))
                    {
                        DeviceReturnError(szFunction, a_apicmd, "invalidJson", null, lResponseCharacterOffset);
                        return (false);
                    }

                    // Update the ApiCmd command object...
                    switch (m_twainlocalsession.GetSessionState())
                    {
                        default:
                            a_apicmd.UpdateUsingIpcData(jsonlookup, false, m_szImagesFolder);
                            break;
                        case SessionState.capturing:
                        case SessionState.draining:
                            a_apicmd.UpdateUsingIpcData(jsonlookup, true, m_szImagesFolder);
                            break;
                    }

                    // Reply to the command with a session object...
                    blSuccess = DeviceUpdateSession(szFunction, a_apicmd, false, null, m_twainlocalsession.GetSessionState(), -1, null);
                    if (!blSuccess)
                    {
                        DeviceReturnError(szFunction, a_apicmd, "critical", null, -1);
                        return (false);
                    }

                    // Parse it...
                    if (!string.IsNullOrEmpty(a_apicmd.HttpResponseData()))
                    {
                        blSuccess = jsonlookup.Load(a_apicmd.HttpResponseData(), out lResponseCharacterOffset);
                        if (!blSuccess)
                        {
                            Log.Error(szFunction + ": error parsing the reply...");
                            return (false);
                        }
                    }
                }

                #endregion

                //////////////////////////////////////////////////////////////////////
                // This path is taken for waitForEvents
                //////////////////////////////////////////////////////////////////////
                #region waitForEvents

                // Handle waitForEvents...
                else
                {
                    // Log a header...
                    if (a_blGetSession)
                    {
                        Log.Info("");
                        Log.Info("http>>> waitForEvents (response)");
                    }

                    // Create an event...
                    apicmdEvent = null;

                    // Update our session revision (always do this)...
                    m_twainlocalsession.SetWaitForEventsSessionRevision(a_apicmd.GetJsonReceived("params.sessionRevision"));

                    // Stock it, if asked to...
                    if (a_blGetSession)
                    {
                        // Create an event...
                        apicmdEvent = new ApiCmd(null);

                        // Get the current session info...
                        m_twainlocalsession.GetIpcTwainDirectOnTwain().Write
                        (
                            "{" +
                            "\"method\":\"getSession\"" +
                            "}"
                        );

                        // Get the result...
                        JsonLookup jsonlookup = new JsonLookup();
                        szIpc = m_twainlocalsession.GetIpcTwainDirectOnTwain().Read();
                        if (!jsonlookup.Load(szIpc, out lResponseCharacterOffset))
                        {
                            DeviceReturnError(szFunction, a_apicmd, "invalidJson", null, lResponseCharacterOffset);
                            return (false);
                        }

                        // TBD: some kind of check to see if the session data
                        // is different from the last call...

                        // Update the ApiCmd command object...
                        switch (m_twainlocalsession.GetSessionState())
                        {
                            default:
                                apicmdEvent.UpdateUsingIpcData(jsonlookup, false, m_szImagesFolder);
                                break;
                            case SessionState.capturing:
                            case SessionState.draining:
                                apicmdEvent.UpdateUsingIpcData(jsonlookup, true, m_szImagesFolder);
                                break;
                        }

                        // Bump up the session number...
                        m_twainlocalsession.SetSessionRevision(m_twainlocalsession.GetSessionRevision() + 1);
                    }

                    // Reply to the command, but only if we have
                    // pending data...
                    blSuccess = DeviceUpdateSession(szFunction, a_apicmd, true, apicmdEvent, m_twainlocalsession.GetSessionState(), m_twainlocalsession.GetSessionRevision(), a_szEventName);
                    if (!blSuccess)
                    {
                        DeviceReturnError(szFunction, a_apicmd, "critical", null, -1);
                        return (false);
                    }
                }

                #endregion
            }

            // All done...
            return (true);
        }

        /// <summary>
        /// Get an image...
        /// </summary>
        /// <param name="a_apicmd">command object</param>
        /// <returns>true on success</returns>
        private bool DeviceScannerReadImageBlock(ref ApiCmd a_apicmd)
        {
            bool blSuccess;
            bool blWithMetadata;
            long lResponseCharacterOffset;
            string szIpc;
            string szFunction = "DeviceScannerReadImageBlock";

            // Protect our stuff...
            lock (m_objectLock)
            {
                // Refresh our timer...
                DeviceSessionRefreshTimer();

                // State check...
                switch (m_twainlocalsession.GetSessionState())
                {
                    // These are okay...
                    case SessionState.capturing:
                    case SessionState.draining:
                    case SessionState.closed:
                        break;

                    // These are not...
                    case SessionState.ready:
                    case SessionState.noSession:
                    default:
                        DeviceReturnError(szFunction, a_apicmd, "invalidState", null, -1);
                        break;
                }

                // Do we want the metadata?
                blWithMetadata = false;
                if (a_apicmd.GetJsonReceived("params.withMetadata") == "true")
                {
                    blWithMetadata = true;
                }

                // Pass the data along to our helper...
                m_twainlocalsession.GetIpcTwainDirectOnTwain().Write
                (
                    "{" +
                    "\"method\":\"readImageBlock\"," +
                    (blWithMetadata ? "\"withMetadata\":true," : "") +
                    "\"imageBlockNum\":\"" + a_apicmd.GetJsonReceived("params.imageBlockNum") + "\"" +
                    "}"
                );

                // Get the result...
                JsonLookup jsonlookup = new JsonLookup();
                szIpc = m_twainlocalsession.GetIpcTwainDirectOnTwain().Read();
                if (!jsonlookup.Load(szIpc, out lResponseCharacterOffset))
                {
                    DeviceReturnError(szFunction, a_apicmd, "invalidJson", null, lResponseCharacterOffset);
                    return (false);
                }

                // Update the ApiCmd command object...
                switch (m_twainlocalsession.GetSessionState())
                {
                    default:
                        a_apicmd.UpdateUsingIpcData(jsonlookup, false, m_szImagesFolder);
                        break;
                    case SessionState.capturing:
                    case SessionState.draining:
                        a_apicmd.UpdateUsingIpcData(jsonlookup, true, m_szImagesFolder);
                        break;
                }

                // Reply to the command with a session object...
                blSuccess = DeviceUpdateSession(szFunction, a_apicmd, false, null, m_twainlocalsession.GetSessionState(), -1, null);
                if (!blSuccess)
                {
                    DeviceReturnError(szFunction, a_apicmd, "critical", null, -1);
                    return (false);
                }

                // Parse it...
                if (!string.IsNullOrEmpty(a_apicmd.HttpResponseData()))
                {
                    blSuccess = jsonlookup.Load(a_apicmd.HttpResponseData(), out lResponseCharacterOffset);
                    if (!blSuccess)
                    {
                        Log.Error(szFunction + ": error parsing the reply...");
                        return (false);
                    }
                }
            }

            // All done...
            return (true);
        }

        /// <summary>
        /// Get TWAIN Direct metadata for an image...
        /// </summary>
        /// <param name="a_apicmd">our command object</param>
        /// <returns>true on success</returns>
        private bool DeviceScannerReadImageBlockMetadata(ref ApiCmd a_apicmd)
        {
            bool blSuccess;
            bool blWithThumbnail = false;
            long lResponseCharacterOffset;
            string szIpc;
            string szFunction = "DeviceScannerReadImageBlockMetadata";

            // Protect our stuff...
            lock (m_objectLock)
            {
                // Refresh our timer...
                DeviceSessionRefreshTimer();

                // State check...
                switch (m_twainlocalsession.GetSessionState())
                {
                    // These are okay...
                    case SessionState.capturing:
                    case SessionState.draining:
                    case SessionState.closed:
                        break;

                    // These are not...
                    case SessionState.ready:
                    case SessionState.noSession:
                    default:
                        DeviceReturnError(szFunction, a_apicmd, "invalidState", null, -1);
                        return (false);
                }

                // Do we want a thumbnail?
                if (a_apicmd.GetJsonReceived("params.withThumbnail") == "true")
                {
                    blWithThumbnail = true;
                }

                // Pass this along to our helper process...
                m_twainlocalsession.GetIpcTwainDirectOnTwain().Write
                (
                    "{" +
                    "\"method\":\"readImageBlockMetadata\"," +
                    "\"imageBlockNum\":\"" + a_apicmd.GetJsonReceived("params.imageBlockNum") + "\"," +
                    "\"withThumbnail\":" + (blWithThumbnail ? "true" : "false") +
                    "}"
                );

                // Get the result...
                JsonLookup jsonlookup = new JsonLookup();
                szIpc = m_twainlocalsession.GetIpcTwainDirectOnTwain().Read();
                if (!jsonlookup.Load(szIpc, out lResponseCharacterOffset))
                {
                    DeviceReturnError(szFunction, a_apicmd, "invalidJson", null, lResponseCharacterOffset);
                    return (false);
                }

                // Update the ApiCmd command object...
                switch (m_twainlocalsession.GetSessionState())
                {
                    default:
                        a_apicmd.UpdateUsingIpcData(jsonlookup, false, m_szImagesFolder);
                        break;
                    case SessionState.capturing:
                    case SessionState.draining:
                        a_apicmd.UpdateUsingIpcData(jsonlookup, true, m_szImagesFolder);
                        break;
                }

                // Reply to the command with a session object...
                blSuccess = DeviceUpdateSession(szFunction, a_apicmd, false, null, m_twainlocalsession.GetSessionState(), -1, null);
                if (!blSuccess)
                {
                    DeviceReturnError(szFunction, a_apicmd, "critical", null, -1);
                    return (false);
                }

                // Parse it...
                if (!string.IsNullOrEmpty(a_apicmd.HttpResponseData()))
                {
                    blSuccess = jsonlookup.Load(a_apicmd.HttpResponseData(), out lResponseCharacterOffset);
                    if (!blSuccess)
                    {
                        Log.Error(szFunction + ": error parsing the reply...");
                        return (false);
                    }
                }
            }

            // All done...
            return (true);
        }

        /// <summary>
        /// Release an image or a range of images...
        /// </summary>
        /// <param name="a_apicmd">command object</param>
        /// <returns>true on success</returns>
        private bool DeviceScannerReleaseImageBlocks(ref ApiCmd a_apicmd)
        {
            bool blSuccess;
            long lResponseCharacterOffset;
            string szIpc;
            string szFunction = "DeviceScannerReleaseImageBlocks";

            // Protect our stuff...
            lock (m_objectLock)
            {
                // Refresh our timer...
                DeviceSessionRefreshTimer();

                // State check...
                switch (m_twainlocalsession.GetSessionState())
                {
                    // These are okay...
                    case SessionState.capturing:
                    case SessionState.draining:
                    case SessionState.closed:
                        break;

                    // These are not...
                    case SessionState.ready:
                    case SessionState.noSession:
                    default:
                        DeviceReturnError(szFunction, a_apicmd, "invalidState", null, -1);
                        return (false);
                }

                // Get the current session info...
                m_twainlocalsession.GetIpcTwainDirectOnTwain().Write
                (
                    "{" +
                    "\"method\":\"releaseImageBlocks\"," +
                    "\"imageBlockNum\":\"" + a_apicmd.GetJsonReceived("params.imageBlockNum") + "\"," +
                    "\"lastImageBlockNum\":\"" + a_apicmd.GetJsonReceived("params.lastImageBlockNum") + "\"" +
                    "}"
                );

                // Get the result...
                JsonLookup jsonlookup = new JsonLookup();
                szIpc = m_twainlocalsession.GetIpcTwainDirectOnTwain().Read();
                blSuccess = jsonlookup.Load(szIpc, out lResponseCharacterOffset);
                if (!blSuccess)
                {
                    DeviceReturnError(szFunction, a_apicmd, "invalidJson", null, lResponseCharacterOffset);
                    return (false);
                }

                // Update the ApiCmd command object...
                switch (m_twainlocalsession.GetSessionState())
                {
                    default:
                        a_apicmd.UpdateUsingIpcData(jsonlookup, false, m_szImagesFolder);
                        break;
                    case SessionState.capturing:
                    case SessionState.draining:
                        a_apicmd.UpdateUsingIpcData(jsonlookup, true, m_szImagesFolder);
                        break;
                }

                // if the session has been closed and we have no more images,
                // then we need to close down twaindirect on twain...

                // Reply to the command with a session object...
                blSuccess = DeviceUpdateSession(szFunction, a_apicmd, false, null, m_twainlocalsession.GetSessionState(), -1, null);
                if (!blSuccess)
                {
                    DeviceReturnError(szFunction, a_apicmd, "critical", null, -1);
                    return (false);
                }

                // Parse it...
                if (!string.IsNullOrEmpty(a_apicmd.HttpResponseData()))
                {
                    blSuccess = jsonlookup.Load(a_apicmd.HttpResponseData(), out lResponseCharacterOffset);
                    if (!blSuccess)
                    {
                        Log.Error(szFunction + ": error parsing the reply...");
                        return (false);
                    }
                }

                // If we're out of imageBlocks, and can't get anymore,
                // then transition to noSession or ready...
                if (string.IsNullOrEmpty(a_apicmd.GetImageBlocks()))
                {
                    // Set the flag if we can't get any more images...
                    if ((m_twainlocalsession != null) && File.Exists(Path.Combine(m_szImagesFolder, "imageBlocksDrained.meta")))
                    {
                        // Make a note of this...
                        m_twainlocalsession.SetSessionImageBlocksDrained(true);

                        // Where we go next depends on our current state...
                        switch (GetState())
                        {
                            default:
                                // Ignore it...
                                break;
                            case "draining":
                                SetSessionState(SessionState.ready);
                                break;
                            case "closed":
                                SetSessionState(SessionState.noSession);
                                DeviceShutdownTwainDirectOnTwain(false);
                                break;
                        }
                    }
                }
            }

            // All done...
            return (true);
        }

        /// <summary>
        /// Set the TWAIN Direct options...
        /// </summary>
        /// <param name="a_jsonlookup">data from the application/cloud</param>
        /// <returns>true on success</returns>
        private bool DeviceScannerSendTask(ref ApiCmd a_apicmd)
        {
            bool blSuccess;
            long lResponseCharacterOffset;
            string szIpc;
            string szStatus;
            string szFunction = "DeviceScannerSendTask";

            // Protect our stuff...
            lock (m_objectLock)
            {
                // Refresh our timer...
                DeviceSessionRefreshTimer();

                // State check, we're allowing this to happen in more
                // than just the ready state to support custom vendor
                // actions.  The current TWAIN Direct actions can only
                // be used in the Ready state...
                switch (m_twainlocalsession.GetSessionState())
                {
                    // These are okay...
                    case SessionState.ready:
                        break;

                    // TBD
                    // These need to be checked to see if they are all vendor specific actions...
                    case SessionState.capturing:
                    case SessionState.draining:
                    case SessionState.closed:
                        break;

                    // These are not...
                    case SessionState.noSession:
                    default:
                        DeviceReturnError(szFunction, a_apicmd, "invalidState", null, -1);
                        return (false);
                }

                // Set the TWAIN Direct options...
                m_twainlocalsession.GetIpcTwainDirectOnTwain().Write
                (
                    "{" +
                    "\"method\":\"sendTask\"," +
                    "\"task\":" + a_apicmd.GetJsonReceived("params.task") +
                    "}"
                );

                // Get the result...
                JsonLookup jsonlookup = new JsonLookup();
                szIpc = m_twainlocalsession.GetIpcTwainDirectOnTwain().Read();
                blSuccess = jsonlookup.Load(szIpc, out lResponseCharacterOffset);
                if (!blSuccess)
                {
                    DeviceReturnError(szFunction, a_apicmd, "invalidJson", null, lResponseCharacterOffset);
                    return (false);
                }

                // Check the status...
                szStatus = jsonlookup.Get("status");
                if (szStatus != "success")
                {
                    switch (szStatus)
                    {
                        default:
                            DeviceReturnError(szFunction, a_apicmd, szStatus, null, -1);
                            break;
                        case "invalidCapturingOptions":
                            DeviceReturnError(szFunction, a_apicmd, "invalidTask", jsonlookup.Get("taskReply"), -1);
                            break;
                    }
                    return (false);
                }

                // Update the ApiCmd command object...
                switch (m_twainlocalsession.GetSessionState())
                {
                    default:
                        a_apicmd.UpdateUsingIpcData(jsonlookup, false, m_szImagesFolder);
                        break;
                    case SessionState.capturing:
                    case SessionState.draining:
                        a_apicmd.UpdateUsingIpcData(jsonlookup, true, m_szImagesFolder);
                        break;
                }

                // Reply to the command with a session object...
                blSuccess = DeviceUpdateSession(szFunction, a_apicmd, false, null, m_twainlocalsession.GetSessionState(), -1, null);
                if (!blSuccess)
                {
                    DeviceReturnError(szFunction, a_apicmd, "invalidJson", null, lResponseCharacterOffset);
                    return (false);
                }

                // Parse it...
                if (!string.IsNullOrEmpty(a_apicmd.HttpResponseData()))
                {
                    blSuccess = jsonlookup.Load(a_apicmd.HttpResponseData(), out lResponseCharacterOffset);
                    if (!blSuccess)
                    {
                        Log.Error(szFunction + ": error parsing the reply...");
                        return (false);
                    }
                }
            }

            // All done...
            return (true);
        }

        /// <summary>
        /// Handle changes to the imageBlocks folder...
        /// </summary>
        /// <param name="source"></param>
        /// <param name="e"></param>
        private void OnChangedImageBlocks(object source, FileSystemEventArgs e)
        {
            FileSystemWatcherHelper filesystemwatcherhelper = (FileSystemWatcherHelper)source;
            ApiCmd apicmdEvent = filesystemwatcherhelper.GetTwainLocalScanner().GetApiCmdEvent();
            if (apicmdEvent == null)
            {
                Log.Error("The session has been modified, but we have no apicmdEvent, did you forget to run DeviceScannerWaitForEvents?");
                return;
            }
            filesystemwatcherhelper.GetTwainLocalScanner().DeviceScannerGetSession(ref apicmdEvent, true, true, "imageBlocks");
        }

        /// <summary>
        /// Start capturing images...
        /// </summary>
        /// <param name="a_apicmd">the command we're processing</param>
        /// <returns>true on success</returns>
        private bool DeviceScannerStartCapturing(ref ApiCmd a_apicmd)
        {
            bool blSuccess;
            long lResponseCharacterOffset;
            string szIpc;
            string szFunction = "DeviceScannerStartCapturing";

            // Protect our stuff...
            lock (m_objectLock)
            {
                // Refresh our timer...
                DeviceSessionRefreshTimer();

                // State check...
                if (m_twainlocalsession.GetSessionState() != SessionState.ready)
                {
                    DeviceReturnError(szFunction, a_apicmd, "invalidState", null, -1);
                    return (false);
                }

                // We start by assuming that any problems with the scanner have
                // been resoved by the user...
                m_twainlocalsession.SetSessionStatusSuccess(true);
                m_twainlocalsession.SetSessionStatusDetected("nominal");

                // Start capturing...
                m_twainlocalsession.GetIpcTwainDirectOnTwain().Write
                (
                    "{" +
                    "\"method\":\"startCapturing\"" +
                    "}"
                );

                // Get the result...
                JsonLookup jsonlookup = new JsonLookup();
                szIpc = m_twainlocalsession.GetIpcTwainDirectOnTwain().Read();
                blSuccess = jsonlookup.Load(szIpc, out lResponseCharacterOffset);
                if (!blSuccess)
                {
                    DeviceReturnError(szFunction, a_apicmd, "invalidJson", null, lResponseCharacterOffset);
                    return (false);
                }

                // Update the ApiCmd command object...
                a_apicmd.UpdateUsingIpcData(jsonlookup, true, m_szImagesFolder);

                // Reply to the command with a session object...
                blSuccess = DeviceUpdateSession(szFunction, a_apicmd, false, null, SessionState.capturing, -1, null);
                if (!blSuccess)
                {
                    DeviceReturnError(szFunction, a_apicmd, "critical", null, -1);
                    return (false);
                }

                // Parse it...
                if (!string.IsNullOrEmpty(a_apicmd.HttpResponseData()))
                {
                    blSuccess = jsonlookup.Load(a_apicmd.HttpResponseData(), out lResponseCharacterOffset);
                    if (!blSuccess)
                    {
                        Log.Error(szFunction + ": error parsing the reply...");
                        return (false);
                    }
                }

                // Start monitoring for imageblocks...
                // TBD: getting the images folder this way is a hack
                if (!Directory.Exists(m_szImagesFolder))
                {
                    try
                    {
                        Directory.CreateDirectory(m_szImagesFolder);
                    }
                    catch (Exception exception)
                    {
                        Log.Error(szFunction + ": CreateDirectory failed..." + exception.Message);
                        return (false);
                    }
                }
                m_twainlocalsession.SetFileSystemWatcherHelperImageBlocks(new FileSystemWatcherHelper(this));
                m_twainlocalsession.GetFileSystemWatcherHelperImageBlocks().Path = m_szImagesFolder;
                m_twainlocalsession.GetFileSystemWatcherHelperImageBlocks().Filter = "*.meta";
                m_twainlocalsession.GetFileSystemWatcherHelperImageBlocks().IncludeSubdirectories = true;
                m_twainlocalsession.GetFileSystemWatcherHelperImageBlocks().NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName;
                m_twainlocalsession.GetFileSystemWatcherHelperImageBlocks().Changed += new FileSystemEventHandler(OnChangedImageBlocks);
                m_twainlocalsession.GetFileSystemWatcherHelperImageBlocks().EnableRaisingEvents = true;
            }

            // All done...
            return (true);
        }

        /// <summary>
        /// Gracefully stop capturing images...
        /// </summary>
        /// <param name="a_apicmd">command object</param>
        /// <returns>true on success</returns>
        private bool DeviceScannerStopCapturing(ref ApiCmd a_apicmd)
        {
            bool blSuccess;
            long lResponseCharacterOffset;
            string szIpc;
            string szFunction = "DeviceScannerStopCapturing";

            // Protect our stuff...
            lock (m_objectLock)
            {
                // Refresh our timer...
                DeviceSessionRefreshTimer();

                // State check...
                switch (m_twainlocalsession.GetSessionState())
                {
                    // These are okay...
                    case SessionState.capturing:
                    case SessionState.draining:
                        break;

                    // These are not...
                    case SessionState.closed:
                    case SessionState.noSession:
                    case SessionState.ready:
                    default:
                        DeviceReturnError(szFunction, a_apicmd, "invalidState", null, -1);
                        return (false);
                }

                // Stop capturing...
                m_twainlocalsession.GetIpcTwainDirectOnTwain().Write
                (
                    "{" +
                    "\"method\":\"stopCapturing\"" +
                    "}"
                );

                // Get the result...
                JsonLookup jsonlookup = new JsonLookup();
                szIpc = m_twainlocalsession.GetIpcTwainDirectOnTwain().Read();
                blSuccess = jsonlookup.Load(szIpc, out lResponseCharacterOffset);
                if (!blSuccess)
                {
                    DeviceReturnError(szFunction, a_apicmd, "invalidJson", null, lResponseCharacterOffset);
                    return (false);
                }

                // Update the ApiCmd command object...
                switch (m_twainlocalsession.GetSessionState())
                {
                    default:
                        a_apicmd.UpdateUsingIpcData(jsonlookup, false, m_szImagesFolder);
                        break;
                    case SessionState.capturing:
                    case SessionState.draining:
                        a_apicmd.UpdateUsingIpcData(jsonlookup, true, m_szImagesFolder);
                        break;
                }

                // If we're out of images, we can go to a ready state, otherwise go to
                // draining...
                if (a_apicmd.GetImageBlocksDrained())
                {
                    SetSessionState(SessionState.ready);
                }
                else
                {
                    SetSessionState(SessionState.draining);
                }

                // Reply to the command with a session object...
                blSuccess = DeviceUpdateSession(szFunction, a_apicmd, false, null, m_twainlocalsession.GetSessionState(), -1, null);
                if (!blSuccess)
                {
                    DeviceReturnError(szFunction, a_apicmd, "critical", null, -1);
                    return (false);
                }

                // Parse it...
                if (!string.IsNullOrEmpty(a_apicmd.HttpResponseData()))
                {
                    blSuccess = jsonlookup.Load(a_apicmd.HttpResponseData(), out lResponseCharacterOffset);
                    if (!blSuccess)
                    {
                        Log.Error(szFunction + ": error parsing the reply...");
                        return (false);
                    }
                }
            }

            // All done...
            return (true);
        }

        /// <summary>
        /// We don't need a thread to report back events.  What we
        /// need is the ApiCmd for the outstanding request.  If we
        /// have pending event data, we respond immediately.  If
        /// there is no event data, we just need to remember the
        /// ApiCmd, so we can use it later when events do show up.
        /// 
        /// This works because we only remove events based on an
        /// expiration time, or when we get a waitForEvents command
        /// that tells us what the client has received.  We never
        /// clear an event after sending it.
        /// 
        /// We don't refresh the session time with waitForEvents,
        /// otherwise we'd never expire... :)
        /// </summary>
        /// <param name="a_apicmd">our command object</param>
        /// <returns>true on success</returns>
        private bool DeviceScannerWaitForEvents(ref ApiCmd a_apicmd)
        {
            bool blSuccess;
            string szFunction = "DeviceScannerWaitForEvents";

            // Squirrel this away...
            m_apicmdEvent = a_apicmd;

            // Update events...
            blSuccess = DeviceScannerGetSession(ref a_apicmd, true, false, null);
            if (!blSuccess)
            {
                DeviceReturnError(szFunction, a_apicmd, "critical", null, -1);
                return (false);
            }

            // All done...
            return (true);
        }

        #endregion


        ///////////////////////////////////////////////////////////////////////////////
        // Private Definitions...
        ///////////////////////////////////////////////////////////////////////////////
        #region Private Definitions...

        /// <summary>
        /// Ways of getting to the server...
        /// </summary>
        private enum HttpMethod
        {
            Undefined,
            Curl,
            WebRequest
        }

        /// <summary>
        /// TWAIN Local Scanner API session states
        /// noSession - we don't have a session with a client
        /// ready - we have a session, but we're not scanning or transfering images
        /// capturing - we're capturing and transfering images
        /// draining - we're transfering images (go to ready when done)
        /// closed - we're transfering images (go to noSession when done)
        /// </summary>
        private enum SessionState
        {
            noSession,
            ready,
            capturing,
            draining,
            closed
        }

        /// <summary>
        /// Information for waiting for events...
        /// </summary>
        private sealed class WaitForEventsInfo : IDisposable
        {
            /// <summary>
            /// Constructor...
            /// </summary>
            public WaitForEventsInfo()
            {
                m_lapicmdEvents = new List<ApiCmd>();
                m_objectlapicmdLock = new object();
            }

            /// <summary>
            /// Destructor...
            /// </summary>
            ~WaitForEventsInfo()
            {
                Dispose(false);
            }

            /// <summary>
            /// Cleanup...
            /// </summary>
            public void Dispose()
            {
                Dispose(true);
                GC.SuppressFinalize(this);
            }

            /// <summary>
            /// Cleanup...
            /// </summary>
            /// <param name="a_blDisposing">true if we need to clean up managed resources</param>
            internal void Dispose(bool a_blDisposing)
            {
                if (m_apicmd != null)
                {
                    m_apicmd.HttpAbort();
                    m_apicmd = null;
                }
                if (m_threadCommunication != null)
                {
                    m_threadCommunication.Abort();
                    m_threadCommunication.Join();
                    m_threadCommunication = null;
                }
                if (m_threadProcessing != null)
                {
                    m_threadProcessing.Abort();
                    m_threadProcessing.Join();
                    m_threadProcessing = null;
                }
            }

            // Stuff needed for the thread...
            public ApiCmd m_apicmd;

            /// <summary>
            /// The communication thread issues the long poll HTTP
            /// request, which is sent to the processing thread...
            /// </summary>
            public Thread m_threadCommunication;

            /// <summary>
            /// The processing thread does the real work...
            /// </summary>
            public Thread m_threadProcessing;

            // Arguments for the HTTP request...
            public string m_szReason;
            public Dnssd.DnssdDeviceInfo m_dnssddeviceinfo;
            public string m_szUri;
            public string m_szMethod;
            public string[] m_aszHeader;
            public string m_szData;
            public string m_szUploadFile;
            public string m_szOutputFile;
            public int m_iTimeout;
            public ApiCmd.HttpReplyStyle m_httpreplystyle;

            /// <summary>
            /// Our list of events.  If they come in fast, before we have
            /// a chance to process any of them, they'll bunch up here.
            /// Note that this list is protected by a lock...
            /// </summary>
            public List<ApiCmd> m_lapicmdEvents;
            public object m_objectlapicmdLock;
        }

        #endregion


        ///////////////////////////////////////////////////////////////////////////////
        // Private Attributes...
        // All of the members in this section must be specific to the device
        // and not to the session.  Session stuff goes into TwainLocalSession.
        ///////////////////////////////////////////////////////////////////////////////
        #region Private Attributes...

        /// <summary>
        /// Use this with the /privet/info and /privet/infoex commands...
        /// </summary>
        private TwainLocalSession m_twainlocalsessionInfo;

        /// <summary>
        /// All of the data we need for /privet/twaindirect/session...
        /// </summary>
        private TwainLocalSession m_twainlocalsession;

        /// <summary>
        /// Our session timer for /privet/twaindirect/session...
        /// </summary>
        private Timer m_timerSession;

        /// <summary>
        /// Something we can lock...
        /// </summary>
        private object m_objectLock;

        /// <summary>
        /// Our HTTP server, all sessions must past through
        /// one server...
        /// </summary>
        private HttpServer m_httpserver;

        /// <summary>
        /// We only need this value long enough to get it from
        /// info or infoex to createSession, and specifically
        /// into the TwainLocalSession object, which will maintain
        /// it for the life of the session...
        /// </summary>
        private string m_szXPrivetToken;

        /// <summary>
        /// This value is generated whenever the TWAIN Local
        /// Scanner object is created, and it exists only for
        /// so long as the object exists.  We use this to
        /// generatre our X-Privet-Token.  Knowing this value
        /// allows us to validate it without having to keep
        /// a table around...
        /// </summary>
        private string m_szDeviceSecret;

        /// <summary>
        /// A place to store data, like logs and stuff...
        /// </summary>
        private string m_szWriteFolder;

        /// <summary>
        /// A place to store images and metadata...
        /// </summary>
        private string m_szImagesFolder;

        /// <summary>
        /// Our current platform...
        /// </summary>
        private static Platform ms_platform = Platform.UNKNOWN;

        /// <summary>
        /// Use this to confirm a scan request...
        /// </summary>
        private ConfirmScan m_confirmscan;

        /// <summary>
        /// So we can have a bigger form...
        /// </summary>
        private float m_fConfirmScanScale;

        /// <summary>
        /// Event callback function...
        /// </summary>
        private EventCallback m_eventcallback;

        /// <summary>
        /// Caller's object for the event callback function...
        /// </summary>
        private object m_objectEventCallback;

        /// <summary>
        /// Optional callback for displaying text, this could
        /// be useful for debugging...
        /// </summary>
        private DisplayCallback m_displaycallback;

        /// <summary>
        /// Command timeout, this should be short (and in milliseconds)...
        /// </summary>
        private int m_iHttpTimeoutCommand;

        /// <summary>
        /// Data timeout, this should be long (and in milliseconds)...
        /// </summary>
        private int m_iHttpTimeoutData;

        /// <summary>
        /// Event timeout, this should be long (and in milliseconds)...
        /// </summary>
        private int m_iHttpTimeoutEvent;

        /// <summary>
        /// Idle time before a session times out (in milliseconds)...
        /// </summary>
        private long m_lSessionTimeout;

        /// <summary>
        /// Event info...
        /// </summary>
        private WaitForEventsInfo m_waitforeventsinfo;

        /// <summary>
        /// Our signal to the client that an event has arrived...
        /// </summary>
        private AutoResetEvent m_autoreseteventWaitForEvents;
        private AutoResetEvent m_autoreseteventWaitForEventsProcessing;

        /// <summary>
        /// The long poll is on this guy, we'll respond to him when
        /// and if we have an event...
        /// </summary>
        private ApiCmd m_apicmdEvent;

        #endregion


        ///////////////////////////////////////////////////////////////////////////////
        // Class: File System Watcher
        // We use this to detect when TwainDirectOnTwain has added new images and
        // metadata to the image folder maintained by TwainDirectScanner.
        ///////////////////////////////////////////////////////////////////////////////
        #region Class: File System Watcher

        // We need to associate some information with our file system
        // watcher.  Specifically, the TwainLocalSscanner and the
        // pending ApiCmd used for events...
        private class FileSystemWatcherHelper : FileSystemWatcher
        {
            public FileSystemWatcherHelper(TwainLocalScanner a_twainlocalscanner)
            {
                m_twainlocalsscanner = a_twainlocalscanner;
            }

            public TwainLocalScanner GetTwainLocalScanner()
            {
                return (m_twainlocalsscanner);
            }

            private TwainLocalScanner m_twainlocalsscanner;
        }

        #endregion


        ///////////////////////////////////////////////////////////////////////////////
        // Class: Twain Local Session
        // Information about a session.  In theory we should be able to have more
        // than one of these, with one of them owning the scanner transport, and
        // the others finishing up transferring images.
        ///////////////////////////////////////////////////////////////////////////////
        #region Class: Twain Local Session

        /// <summary>
        /// TWAIN Local session information that we need to keep track of...
        /// </summary>
        private class TwainLocalSession : IDisposable
        {
            ///////////////////////////////////////////////////////////////////////////
            // Public Methods
            ///////////////////////////////////////////////////////////////////////////
            #region Public Methods

            /// <summary>
            /// Init stuff...
            /// </summary>
            /// <param name="a_szXPrivetToken">the privet token for this session</param>
            public TwainLocalSession
            (
                string a_szXPrivetToken
            )
            {
                // Our state...
                m_sessionstate = SessionState.noSession;

                // The session object...
                m_szSessionId = null;
                m_szCallersHostName = null;
                m_lSessionRevision = 0;
                m_lWaitForEventsSessionRevision = 0;
                m_szSessionSnapshot = "";
                m_alSessionImageBlocks = null;
                SetSessionImageBlocksDrained(true); // we start empty and ready to scoot
                m_szXPrivetToken = a_szXPrivetToken;

                // We assume all is well until told otherwise...
                m_blSessionStatusSuccess = true;
                m_szSessionStatusDetected = "nominal";

                // Metadata...
                m_szMetadata = null;

                // Events, we're going with a fixed size, we
                // can grow this dynamically, if needed, but
                // we'd like to avoid that.  Data is always
                // contiguous, starting at 0, with nulls for
                // unused entries...
                m_aapicmdEvents = new ApiCmd[32];

                // The place we'll keep our device information...
                m_deviceregister = new DeviceRegister();

                // Notification when the session revision changes...
                m_autoreseteventWaitForSessionUpdate = new AutoResetEvent(false);
            }

            /// <summary>
            /// Destructor...
            /// </summary>
            ~TwainLocalSession()
            {
                Dispose(false);
            }

            /// <summary>
            /// Cleanup...
            /// </summary>
            public void Dispose()
            {
                Dispose(true);
                GC.SuppressFinalize(this);
            }

            /// <summary>
            /// Create a unique command id...
            /// </summary>
            /// <returns>the device id</returns>
            public string ClientCreateCommandId()
            {
                return (Guid.NewGuid().ToString());
            }

            /// <summary>
            /// Allow the caller to kick us out of a wait...
            /// </summary>
            public void ClientWaitForSessionUpdateForceSet()
            {
                if (m_autoreseteventWaitForSessionUpdate != null)
                {
                    m_autoreseteventWaitForSessionUpdate.Set();
                }
            }

            /// <summary>
            /// Wait for the session object to be updated, this is done
            /// by comparing the current session.revision number to the
            /// session.revision from the last command or event.
            /// </summary>
            /// <param name="a_lMilliseconds">milliseconds to wait for the update</param>
            /// <returns>true if an update was detected, false if the command timed out</returns>
            public bool ClientWaitForSessionUpdate(long a_lMilliseconds)
            {
                bool blSignaled;

                // Wait for it...
                blSignaled = m_autoreseteventWaitForSessionUpdate.WaitOne((int)a_lMilliseconds);
                m_autoreseteventWaitForSessionUpdate.Reset();

                // All done...
                return (blSignaled);
            }

            /// <summary>
            /// Add a device or modify the contents of an existing device.  We
            /// add data in bits and pieces, so expect to see this call made
            /// more than once.  We use two keys: the device name and the device
            /// id...
            /// </summary>
            /// <param name="a_szTwainLocalTy">TWAIN Local ty= field</param>
            /// <param name="a_szTwainLocalSerialNumber">TWAIN serial number (from CAP_SERIALNUMBER)</param>
            /// <param name="a_szTwainLocalNote">User's friendly name</param>
            /// <param name="a_szScanner">the complete scanner record</param>
            public void DeviceRegisterSet
            (
                string a_szTwainLocalTy,
                string a_szTwainLocalSerialNumber,
                string a_szTwainLocalNote,
                string a_szScanner
            )
            {
                m_deviceregister.Set(a_szTwainLocalTy, a_szTwainLocalSerialNumber, a_szTwainLocalNote, a_szScanner);
            }

            /// <summary>
            /// Get the device id, we're using the instance name for now...
            /// </summary>
            /// <returns>the device id</returns>
            public string DeviceRegisterGetDeviceId()
            {
                return (m_deviceregister.GetTwainLocalInstanceName());
            }

            /// <summary>
            /// Get the scanner's manufacturer...
            /// </summary>
            /// <returns>manufacturer</returns>
            public string DeviceRegisterGetTwainLocalManufacturer()
            {
                return (m_deviceregister.GetTwainInquiryData().GetManufacturer());
            }

            /// <summary>
            /// Get the scanner's product name...
            /// </summary>
            /// <returns>product name</returns>
            public string DeviceRegisterGetTwainLocalProductName()
            {
                return (m_deviceregister.GetTwainInquiryData().GetProductName());
            }

            /// <summary>
            /// Get the contents of the register.txt file...
            /// </summary>
            /// <returns>everything we know about the scanner</returns>
            public string DeviceRegisterGetTwainLocalScanner()
            {
                return (m_deviceregister.GetTwainLocalScanner());
            }

            /// <summary>
            /// Get the scanner's serial number...
            /// </summary>
            /// <returns>serial number</returns>
            public string DeviceRegisterGetTwainLocalSerialNumber()
            {
                return (m_deviceregister.GetTwainInquiryData().GetSerialNumber());
            }

            /// <summary>
            /// Get the scanner's version info...
            /// </summary>
            /// <returns>version info</returns>
            public string DeviceRegisterGetTwainLocalVersion()
            {
                return (m_deviceregister.GetTwainInquiryData().GetVersion());
            }

            /// <summary>
            /// Get the TWAIN Local ty= field
            /// </summary>
            /// <returns>the vendors friendly name</returns>
            public string DeviceRegisterGetTwainLocalTy()
            {
                return (m_deviceregister.GetTwainLocalTy());
            }

            /// <summary>
            /// Get the TWAIN Local instance name...
            /// </summary>
            /// <returns>the mDNS instance name</returns>
            public string DeviceRegisterGetTwainLocalInstanceName()
            {
                return (m_deviceregister.GetTwainLocalInstanceName());
            }

            /// <summary>
            /// Get the TWAIN Local note= field...
            /// </summary>
            /// <returns>the users friendly name</returns>
            public string DeviceRegisterGetTwainLocalNote()
            {
                return (m_deviceregister.GetTwainLocalNote());
            }

            /// <summary>
            /// Load data from a file...
            /// </summary>
            /// <param name="a_szFile">the file to load it from</param>
            /// <returns>try if successful</returns>
            public bool DeviceRegisterLoad(TwainLocalScanner a_twainlocalscanner, string a_szFile)
            {
                return (m_deviceregister.Load(a_szFile));
            }

            /// <summary>
            /// Clear the device register...
            /// </summary>
            public void DeviceRegisterClear()
            {
                m_deviceregister.Clear();
            }

            /// <summary>
            /// Persist the data to a file...
            /// </summary>
            /// <param name="a_szFile">the file to save the data in</param>
            /// <returns>true if successful</returns>
            public bool DeviceRegisterSave(string a_szFile)
            {
                return (m_deviceregister.Save(a_szFile));
            }

            /// <summary>
            /// Get the whole event array...
            /// </summary>
            /// <returns></returns>
            public ApiCmd[] GetApicmdEvents()
            {
                return (m_aapicmdEvents);
            }

            /// <summary>
            /// Get the caller's host name
            /// </summary>
            /// <returns>the caller's host name</returns>
            public string GetCallersHostName()
            {
                return (m_szCallersHostName);
            }

            /// <summary>
            /// Get our file system watcher...
            /// </summary>
            /// <returns>the filesystemwatcher object</returns>
            public FileSystemWatcherHelper GetFileSystemWatcherHelperImageBlocks()
            {
                return (m_filesystemwatcherhelperImageBlocks);
            }

            /// <summary>
            /// Our communication channel to TWAIN Direct on TWAIN...
            /// </summary>
            /// <returns>the ipc object</returns>
            public Ipc GetIpcTwainDirectOnTwain()
            {
                return (m_ipcTwainDirectOnTwain);
            }

            /// <summary>
            /// Get the metadata...
            /// </summary>
            /// <returns>the metadata</returns>
            public string GetMetadata()
            {
                return (m_szMetadata);
            }

            /// <summary>
            /// Our TWAIN Direct on TWAIN process...
            /// </summary>
            /// <returns>the process object</returns>
            public Process GetProcessTwainDirectOnTwain()
            {
                return (m_processTwainDirectOnTwain);
            }

            /// <summary>
            /// Get the session id...
            /// </summary>
            /// <returns>the session id</returns>
            public string GetSessionId()
            {
                return (m_szSessionId);
            }

            /// <summary>
            /// Get the session image blocks drained flag...
            /// </summary>
            /// <returns>true if we're drained</returns>
            public bool GetSessionImageBlocksDrained()
            {
                return (m_blSessionImageBlocksDrained);
            }

            /// <summary>
            /// Get the session revision number...
            /// </summary>
            /// <returns>the session revision number</returns>
            public long GetSessionRevision()
            {
                return (m_lSessionRevision);
            }

            /// <summary>
            /// Get the last session object snapshot...
            /// </summary>
            /// <returns>the session object JSON string</returns>
            public string GetSessionSnapshot()
            {
                return (m_szSessionSnapshot);
            }

            /// <summary>
            /// Get the session state...
            /// </summary>
            /// <returns>the session state</returns>
            public SessionState GetSessionState()
            {
                return (m_sessionstate);
            }

            /// <summary>
            /// The status of the session (really the device)...
            /// </summary>
            /// <returns>false if we need user help</returns>
            public bool GetSessionStatusSuccess()
            {
                return (m_blSessionStatusSuccess);
            }

            /// <summary>
            /// The last detected boo-boo...
            /// </summary>
            /// <returns>the reason m_blSessionStatusSuccess is false</returns>
            public string GetSessionStatusDetected()
            {
                return (m_szSessionStatusDetected);
            }

            /// <summary>
            /// We need to track the session revision that the client sends
            /// to use with waitForEvents, so that we can expire older events
            /// with a minimum of fuss.  This helps with that.
            /// </summary>
            /// <returns>revision from the last waitForEventsCall</returns>
            public long GetWaitForEventsSessionRevision()
            {
                return (m_lWaitForEventsSessionRevision);
            }

            /// <summary>
            /// Get the privet token...
            /// </summary>
            /// <returns>the privet token</returns>
            public string GetXPrivetToken()
            {
                return (m_szXPrivetToken);
            }

            /// <summary>
            /// Reset the session revision...
            /// </summary>
            public void ResetSessionRevision()
            {
                m_lSessionRevision = 0;
                m_lWaitForEventsSessionRevision = 0;
            }

            /// <summary>
            /// Set a item in the event array...
            /// </summary>
            /// <returns></returns>
            public void SetApicmdEvent(long a_lIndex, ApiCmd a_apicmd)
            {
                if ((a_lIndex < 0) || (a_lIndex >= m_aapicmdEvents.Length))
                {
                    Log.Error("SetApicmdEvents: bad index..." + a_lIndex);
                    return;
                }
                m_aapicmdEvents[a_lIndex] = a_apicmd;
            }

            /// <summary>
            /// Set the caller's host name...
            /// </summary>
            /// <param name="a_szCallersHostName">callers host name</param>
            public void SetCallersHostName(string a_szCallersHostName)
            {
                m_szCallersHostName = a_szCallersHostName;
            }

            /// <summary>
            /// Set our file system watcher...
            /// </summary>
            public void SetFileSystemWatcherHelperImageBlocks(FileSystemWatcherHelper a_filesystemwatcherhelperImageBlocks)
            {
                m_filesystemwatcherhelperImageBlocks = a_filesystemwatcherhelperImageBlocks;
            }

            /// <summary>
            /// Our communication channel to TWAIN Direct on TWAIN...
            /// </summary>
            public void SetIpcTwainDirectOnTwain(Ipc a_ipcTwainDirectOnTwain)
            {
                m_ipcTwainDirectOnTwain = a_ipcTwainDirectOnTwain;
            }

            /// <summary>
            /// Set the metadata...
            /// </summary>
            /// <param name="a_szMetadata">the metadata</param>
            public void SetMetadata(string a_szMetadata)
            {
                m_szMetadata = a_szMetadata;
            }

            /// <summary>
            /// Set our TWAIN Direct on TWAIN process...
            /// </summary>
            public void SetProcessTwainDirectOnTwain(Process a_processTwainDirectOnTwain)
            {
                m_processTwainDirectOnTwain = a_processTwainDirectOnTwain;
            }

            /// <summary>
            /// Set the session id...
            /// </summary>
            /// <param name="a_szSessionId">the new session id</param>
            public void SetSessionId(string a_szSessionId)
            {
                m_szSessionId = a_szSessionId;
            }

            /// <summary>
            /// Set the session image blocks drained flag...
            /// </summary>
            /// <param name="a_blSessionImageBlocksDrained">true if drained</param>
            public void SetSessionImageBlocksDrained(bool a_blSessionImageBlocksDrained)
            {
                m_blSessionImageBlocksDrained = a_blSessionImageBlocksDrained;
            }

            /// <summary>
            /// Set the revision...
            /// </summary>
            /// <param name="a_lSessionRevision">new revision</param>
            /// <returns>true if the value is greater than what we had</returns>
            public bool SetSessionRevision(long a_lSessionRevision, bool a_blSetEvent = false)
            {
                // If the session sent to us is less than or equal to
                // what we already have, discard it and let the caller
                // know that we didn't take it.
                if (a_lSessionRevision <= m_lSessionRevision)
                {
                    return (false);
                }

                // Otherwise it's all sunshine and rainbows...
                m_lSessionRevision = a_lSessionRevision;

                // Set the event, if asked to...
                if (a_blSetEvent)
                {
                    m_autoreseteventWaitForSessionUpdate.Set();
                }

                // All done...
                return (true);
            }

            /// <summary>
            /// Sets the session snapshot...
            /// </summary>
            /// <param name="a_szSessionSnapshot">the new session snapshot</param>
            public void SetSessionSnapshot(string a_szSessionSnapshot)
            {
                m_szSessionSnapshot = a_szSessionSnapshot;
            }

            /// <summary>
            /// Set the session state...
            /// </summary>
            /// <param name="a_sessionstate"></param>
            public void SetSessionState(SessionState a_sessionstate)
            {
                // Don't set anything, unless we see a change...
                if (m_sessionstate != a_sessionstate)
                {
                    // Log it...
                    Log.Info("SetSessionState: " + m_sessionstate + " --> " + a_sessionstate);

                    // Set it...
                    m_sessionstate = a_sessionstate;

                    // If we just started capturing, then we can't be drained...
                    if (m_sessionstate == SessionState.capturing)
                    {
                        SetSessionImageBlocksDrained(false);
                    }

                    // Cleanup, we need to do this to make sure that we're
                    // reset if a new session is started, and this is the
                    // most central place to handle it...
                    if (m_sessionstate == SessionState.noSession)
                    {
                        ResetSessionRevision();
                        SetSessionId(null);
                        SetCallersHostName(null);
                        ResetSessionRevision();
                        SetSessionSnapshot("");
                    }
                }
            }

            /// <summary>
            /// Set the status of the session (really the device)...
            /// </summary>
            /// <param name="a_blSessionStatusSuccess">false if the scanner needs attention</param>
            public void SetSessionStatusSuccess(bool a_blSessionStatusSuccess)
            {
                m_blSessionStatusSuccess = a_blSessionStatusSuccess;
            }

            /// <summary>
            /// Set the last detected boo-boo...
            /// </summary>
            /// <param name="a_szSessionStatusDetected">the reason the scanner needs attention</param>
            public void SetSessionStatusDetected(string a_szSessionStatusDetected)
            {
                m_szSessionStatusDetected = a_szSessionStatusDetected;
            }

            /// <summary>
            /// We need to keep track of the session revision sent by the last
            /// waitForEvents, so that we can expire events old than that number.
            /// This allows us to keep our event list cleaner, without a lot of
            /// extra code.  We don't need to be in perfect sync, we just need
            /// to be in the ballpark.
            /// </summary>
            /// <param name="a_szWaitForEventsSessionRevision">sessionRevision in the waitForEvents command</param>
            public void SetWaitForEventsSessionRevision(string a_szWaitForEventsSessionRevision)
            {
                long lWaitForEventsSessionRevision;
                if (long.TryParse(a_szWaitForEventsSessionRevision, out lWaitForEventsSessionRevision))
                {
                    // No going backwards!
                    if (lWaitForEventsSessionRevision > m_lWaitForEventsSessionRevision)
                    {
                        m_lWaitForEventsSessionRevision = lWaitForEventsSessionRevision;
                    }
                }
            }

            #endregion


            ///////////////////////////////////////////////////////////////////////////
            // Internal Methods
            ///////////////////////////////////////////////////////////////////////////
            #region Internal Methods

            /// <summary>
            /// Cleanup...
            /// </summary>
            /// <param name="a_blDisposing">true if we need to clean up managed resources</param>
            internal void Dispose(bool a_blDisposing)
            {
                // Free managed resources...
                if (a_blDisposing)
                {
                    // Wake up anybody checking this event...
                    if (m_autoreseteventWaitForSessionUpdate != null)
                    {
                        m_autoreseteventWaitForSessionUpdate.Set();
                        m_autoreseteventWaitForSessionUpdate.Dispose();
                        m_filesystemwatcherhelperImageBlocks = null;
                    }
                    if (m_filesystemwatcherhelperImageBlocks != null)
                    {
                        m_filesystemwatcherhelperImageBlocks.EnableRaisingEvents = false;
                        m_filesystemwatcherhelperImageBlocks.Dispose();
                        m_filesystemwatcherhelperImageBlocks = null;
                    }
                    if (m_ipcTwainDirectOnTwain != null)
                    {
                        m_ipcTwainDirectOnTwain.Dispose();
                        m_ipcTwainDirectOnTwain = null;
                    }
                    if (m_processTwainDirectOnTwain != null)
                    {
                        try
                        {
                            m_processTwainDirectOnTwain.Kill();
                        }
                        catch
                        {
                            // Not really interested in what we catch.
                            // Unless it's a goretrout... :)
                        }
                        m_processTwainDirectOnTwain.Dispose();
                        m_processTwainDirectOnTwain = null;
                    }
                    if (m_aapicmdEvents != null)
                    {
                        m_aapicmdEvents = null;
                    }
                }
            }

            #endregion


            ///////////////////////////////////////////////////////////////////////////
            // Private Attributes
            ///////////////////////////////////////////////////////////////////////////
            #region Private Attributes

            /// <summary>
            /// JSON IN:  params.sessionId
            /// JSON OUT: results.session.sessionId
            /// This is the unique "secret" id that the scanner provides in response
            /// to a CreateSession command.  The scanner uses it to make sure that
            /// commands that it receives belong to this session...
            /// </summary>
            private string m_szSessionId;

            /// <summary>
            /// JSON OUT:  results.session.revision
            /// Report when a change has been made to the session object.
            /// The most obvious use is when the state changes, or the
            /// imageBlocks data is being updated...
            /// </summary>
            private long m_lSessionRevision;

            /// <summary>
            /// False if the scanner needs some kind of user intervention...
            /// </summary>
            private bool m_blSessionStatusSuccess;

            /// <summary>
            /// If m_blSessionStatusSuccess is false, this value explains
            /// what happened.  In theory one can have a detected value
            /// without m_blSessionStatusSuccess being false, but we have
            /// no plans to do anything like that in the TWAIN Bridge...
            /// </summary>
            private string m_szSessionStatusDetected;

            /// <summary>
            /// Triggered when the session object had been updated...
            /// </summary>
            private AutoResetEvent m_autoreseteventWaitForSessionUpdate;

            /// <summary>
            /// This holds a JSON subset of the session object, so that we can
            /// detect changes and then update the revision number...
            /// </summary>
            private string m_szSessionSnapshot;

            /// <summary>
            /// JSON OUT:  results.session.imageBlocks
            /// Reports the index values of image blocks that are ready for transfer
            /// to the client...
            /// </summary>
            public long[] m_alSessionImageBlocks;

            /// <summary>
            /// true if imageBlocksDrained has been set to true...
            /// </summary>
            private bool m_blSessionImageBlocksDrained;

            /// <summary>
            /// JSON OUT:  results.metadata
            /// Metadata...
            /// </summary>
            private string m_szMetadata;

            /// <summary>
            /// The hostname of our caller, captured during createSession,
            /// and used to check all other session calls...
            /// </summary>
            private string m_szCallersHostName;

            /// <summary>
            /// Persistant device information...
            /// </summary>
            private DeviceRegister m_deviceregister;

            /// <summary>
            /// Our current state...
            /// </summary>
            private SessionState m_sessionstate;

            /// <summary>
            /// The interprocess communication object we
            /// use to talk to the TwainDirect.OnTwain process...
            /// </summary>
            private Ipc m_ipcTwainDirectOnTwain;

            /// <summary>
            /// The session revision included with the last
            /// waitForEvents call...
            /// </summary>
            private long m_lWaitForEventsSessionRevision;

            /// <summary>
            /// The TwainDirect.OnTwain process...
            /// </summary>
            private Process m_processTwainDirectOnTwain;

            /// <summary>
            /// Privet requires this in the header for every
            /// command, except /privet/info and /privet/info (which
            /// return the value used by all other commands).  Google
            /// recommends that it be refreshed every 24 hours,
            /// but this can get weird with long lasting sessions,
            /// so instead we're going to refresh it if it's been
            /// more than two minutes since the last call to
            /// /privet/info or /privet/infoex.  Clients must call
            /// createSession immediately after info.  The token is
            /// stored in TwainLocalSession, so it will be valid for
            /// that session as long as it lasts...
            /// </summary>
            private string m_szXPrivetToken;

            /// <summary>
            /// The thread we use to monitor for changes to the contents
            /// of the imageBlocks folder...
            /// </summary>
            private FileSystemWatcherHelper m_filesystemwatcherhelperImageBlocks;

            /// <summary>
            /// Our list of events, maintained in revision order, from
            /// low to high numbers.
            /// </summary>
            private ApiCmd[] m_aapicmdEvents;

            #endregion
        }

        #endregion
    }
}
