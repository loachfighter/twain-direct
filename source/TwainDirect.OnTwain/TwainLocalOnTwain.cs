﻿///////////////////////////////////////////////////////////////////////////////////////
//
//  TwainDirect.OnTwain.TwainLocalToTwain
//
//  Map TWAIN Local calls to TWAIN calls...
//
///////////////////////////////////////////////////////////////////////////////////////
//  Author          Date            Comment
//  M.McLaughlin    17-Dec-2014     Initial Release
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
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using TwainDirect.Support;
using TWAINWorkingGroup;
using TWAINWorkingGroupToolkit;

namespace TwainDirect.OnTwain
{
    /// <summary>
    /// Map TWAIN Local calls to TWAIN.  This seems like the best way to make
    /// sure we get all the needed data down to this level, however it means
    /// that we have knowledge of our caller at this level, so there will be
    /// some replication if we add support for another communication manager...
    /// </summary>
    internal sealed class TwainLocalOnTwain
    {
        // Public Methods: Run
        #region Public Methods: Run

        /// <summary>
        /// Init stuff...
        /// </summary>
        public TwainLocalOnTwain
        (
            string a_szWriteFolder,
            string a_szImagesFolder,
            string a_szIpc,
            int a_iPid,
            TWAINCSToolkit.RunInUiThreadDelegate a_runinuithreaddelegate,
            object a_objectRunInUiThread,
            IntPtr a_intptrHwnd
        )
        {
            // Remember this stuff...
            m_szWriteFolder = a_szWriteFolder;
            if (!string.IsNullOrEmpty(a_szImagesFolder))
            {
                m_szImagesFolder = a_szImagesFolder;
            }
            else
            {
                a_szImagesFolder = Path.Combine(m_szWriteFolder, "images");
            }
            m_szIpc = a_szIpc;
            m_iPid = a_iPid;
            m_runinuithreaddelegate = a_runinuithreaddelegate;
            m_objectRunInUiThread = a_objectRunInUiThread;
            m_intptrHwnd = a_intptrHwnd;

            // Init stuff...
            m_blFlatbed = false;
            m_blDuplex = false;
        }

        /// <summary>
        /// Run the driver...
        /// </summary>
        /// <returns>true on success</returns>
        public bool Run()
        {
            bool blSuccess;
            bool blRunning = true;
            bool blSetAppCapabilities = false;
            long lResponseCharacterOffset;
            string szJson;
            string szMetadataFile;
            string szThumbnailFile;
            string szSession;
            string szImageFile;
            Ipc ipc;
            ProcessSwordTask processswordtask;
            TwainLocalScanner.ApiStatus apistatus;

            // Pipe mode starting...
            TWAINWorkingGroup.Log.Info("IPC mode starting...");

            // Set up communication with our server process...
            ipc = new Ipc(m_szIpc, false);
            ipc.MonitorPid(m_iPid);
            ipc.Connect();

            // Loopy...
            while (blRunning)
            {
                // Read a command...
                szJson = ipc.Read();
                if (szJson == null)
                {
                    TWAINWorkingGroup.Log.Info("IPC channel disconnected...");
                    break;
                }

                // Parse the command...
                JsonLookup jsonlookup = new JsonLookup();
                if (!jsonlookup.Load(szJson, out lResponseCharacterOffset))
                {
                    continue;
                }

                // Dispatch the command...
                switch (jsonlookup.Get("method"))
                {
                    default:
                        break;

                    case "closeSession":
                        apistatus = DeviceScannerCloseSession(out szSession);
                        if (apistatus == TwainLocalScanner.ApiStatus.success)
                        {
                            blSuccess = ipc.Write
                            (
                                "{" +
                                "\"status\":\"" + apistatus + "\"," +
                                szSession +
                                "}"
                            );
                        }
                        else
                        {
                            blSuccess = ipc.Write
                            (
                                "{" +
                                "\"status\":\"" + apistatus + "\"" +
                                "}"
                            );
                        }
                        if (!blSuccess)
                        {
                            TWAINWorkingGroup.Log.Info("IPC channel disconnected...");
                            blRunning = false;
                        }
                        break;

                    case "createSession":
                        apistatus = DeviceScannerCreateSession(jsonlookup, out szSession);
                        if (apistatus == TwainLocalScanner.ApiStatus.success)
                        {
                            blSuccess = ipc.Write
                            (
                                "{" +
                                "\"status\":\"" + apistatus + "\"," +
                                szSession +
                                "}"
                            );
                        }
                        else
                        {
                            blSuccess = ipc.Write
                            (
                                "{" +
                                "\"status\":\"" + apistatus + "\"" +
                                "}"
                            );
                        }
                        if (!blSuccess)
                        {
                            TWAINWorkingGroup.Log.Info("IPC channel disconnected...");
                            blRunning = false;
                        }
                        break;

                    case "exit":
                        blRunning = false;
                        break;

                    case "getSession":
                        apistatus = DeviceScannerGetSession(out szSession);
                        if (apistatus == TwainLocalScanner.ApiStatus.success)
                        {
                            blSuccess = ipc.Write
                            (
                                "{" +
                                "\"status\":\"" + apistatus + "\"," +
                                szSession +
                                "}"
                            );
                        }
                        else
                        {
                            blSuccess = ipc.Write
                            (
                                "{" +
                                "\"status\":\"" + apistatus + "\"" +
                                "}"
                            );
                        }
                        if (!blSuccess)
                        {
                            TWAINWorkingGroup.Log.Info("IPC channel disconnected...");
                            blRunning = false;
                        }
                        break;

                    case "readImageBlock":
                        apistatus = DeviceScannerReadImageBlock(jsonlookup, out szImageFile, out szMetadataFile);
                        if (apistatus == TwainLocalScanner.ApiStatus.success)
                        {
                            apistatus = DeviceScannerGetSession(out szSession);
                            blSuccess = ipc.Write
                            (
                                "{" +
                                "\"status\":\"" + apistatus + "\"," +
                                ((jsonlookup.Get("withMetadata", false) == "true") ? "\"meta\":\"" + szMetadataFile + "\"," : "") +
                                "\"imageFile\":\"" + szImageFile + "\"" +
                                (!string.IsNullOrEmpty(szSession) ? "," + szSession : "") +
                                "}"
                            );
                        }
                        else
                        {
                            blSuccess = ipc.Write
                            (
                                "{" +
                                "\"status\":\"" + apistatus + "\"" +
                                "}"
                            );
                        }
                        if (!blSuccess)
                        {
                            TWAINWorkingGroup.Log.Info("IPC channel disconnected...");
                            blRunning = false;
                        }
                        break;

                    case "readImageBlockMetadata":
                        apistatus = DeviceScannerReadImageBlockMetadata(jsonlookup, out szMetadataFile, out szThumbnailFile);
                        if (apistatus == TwainLocalScanner.ApiStatus.success)
                        {
                            apistatus = DeviceScannerGetSession(out szSession);
                            blSuccess = ipc.Write
                             (
                                 "{" +
                                 "\"status\":\"" + apistatus + "\"," +
                                 "\"meta\":\"" + szMetadataFile + "\"," +
                                 "\"thumbnailFile\":\"" + szThumbnailFile + "\"" +
                                 (!string.IsNullOrEmpty(szSession) ? "," + szSession : "") +
                                 "}"
                             );
                        }
                        else
                        {
                            blSuccess = ipc.Write
                            (
                                "{" +
                                "\"status\":\"" + apistatus + "\"" +
                                "}"
                            );
                        }
                        if (!blSuccess)
                        {
                            TWAINWorkingGroup.Log.Info("IPC channel disconnected...");
                            blRunning = false;
                        }
                        break;

                    case "releaseImageBlocks":
                        apistatus = DeviceScannerReleaseImageBlocks(jsonlookup, out szSession);
                        if (apistatus == TwainLocalScanner.ApiStatus.success)
                        {
                            blSuccess = ipc.Write
                            (
                                "{" +
                                "\"status\":\"" + apistatus + "\"," +
                                szSession +
                                "}"
                            );
                        }
                        else
                        {
                            blSuccess = ipc.Write
                            (
                                "{" +
                                "\"status\":\"" + apistatus + "\"" +
                                "}"
                            );
                        }
                        if (!blSuccess)
                        {
                            TWAINWorkingGroup.Log.Info("IPC channel disconnected...");
                            blRunning = false;
                        }
                        break;

                    case "sendTask":
                        apistatus = DeviceScannerSendTask(jsonlookup, out processswordtask, ref blSetAppCapabilities);
                        blSuccess = ipc.Write
                        (
                            "{" +
                            "\"status\":\"" + apistatus + "\"," +
                            "\"taskReply\":" + processswordtask.GetTaskReply() +
                            "}"
                        );
                        break;

                    case "startCapturing":
                        apistatus = DeviceScannerStartCapturing(ref blSetAppCapabilities, out szSession);
                        if (apistatus == TwainLocalScanner.ApiStatus.success)
                        {
                            blSuccess = ipc.Write
                            (
                                "{" +
                                "\"status\":\"" + apistatus + "\"," +
                                szSession +
                                "}"
                            );
                        }
                        else
                        {
                            blSuccess = ipc.Write
                            (
                                "{" +
                                "\"status\":\"" + apistatus + "\"" +
                                "}"
                            );
                        }
                        if (!blSuccess)
                        {
                            TWAINWorkingGroup.Log.Info("IPC channel disconnected...");
                            blRunning = false;
                        }
                        break;

                    case "stopCapturing":
                        apistatus = DeviceScannerStopCapturing(out szSession);
                        if (apistatus == TwainLocalScanner.ApiStatus.success)
                        {
                            blSuccess = ipc.Write
                            (
                                "{" +
                                "\"status\":\"" + apistatus + "\"," +
                                szSession +
                                "}"
                            );
                        }
                        else
                        {
                            blSuccess = ipc.Write
                            (
                                "{" +
                                "\"status\":\"" + apistatus + "\"" +
                                "}"
                            );
                        }
                        if (!blSuccess)
                        {
                            TWAINWorkingGroup.Log.Info("IPC channel disconnected...");
                            blRunning = false;
                        }
                        break;
                }
            }

            // All done...
            TWAINWorkingGroup.Log.Info("IPC mode completed...");
            return (true);
        }

        #endregion


        // Private Methods: ReportImage
        #region Private Methods: ReportImage

        /// <summary>
        /// Handle an image...
        /// </summary>
        /// <param name="a_szTag">tag to locate a particular ReportImage call</param>
        /// <param name="a_szDg">Data group that preceeded this call</param>
        /// <param name="a_szDat">Data argument type that preceeded this call</param>
        /// <param name="a_szMsg">Message that preceeded this call</param>
        /// <param name="a_sts">Current status</param>
        /// <param name="a_bitmap">C# bitmap of the image</param>
        /// <param name="a_szFile">File name, if doing a file transfer</param>
        /// <param name="a_szTwimageinfo">Image info or null</param>
        /// <param name="a_abImage">Raw image from transfer</param>
        /// <param name="a_iImageOffset">Byte offset into the raw image</param>
        private TWAINCSToolkit.MSG ReportImage
        (
            string a_szTag,
            string a_szDg,
            string a_szDat,
            string a_szMsg,
            TWAIN.STS a_sts,
            Bitmap a_bitmap,
            string a_szFile,
            string a_szTwimageinfo,
            byte[] a_abImage,
            int a_iImageOffset
        )
        {
            uint uu;
            bool blSuccess;
            string szFile;
            string szImageFile;
            TWAIN.STS sts;
            TWAIN twain;

            // We're processing end of scan...
            if (a_bitmap == null)
            {
                TWAINWorkingGroup.Log.Info("ReportImage: no more images: " + a_szDg + " " + a_szDat + " " + a_szMsg + " " + a_sts);
                m_blCancel = false;
                SetImageBlocksDrained(a_sts);
                return (TWAINCSToolkit.MSG.RESET);
            }

            // Init stuff...
            twain = m_twaincstoolkit.Twain();

            // Get the metadata for TW_IMAGEINFO...
            TWAIN.TW_IMAGEINFO twimageinfo = default(TWAIN.TW_IMAGEINFO);
            if (a_szTwimageinfo != null)
            {
                twain.CsvToImageinfo(ref twimageinfo, a_szTwimageinfo);
            }
            else
            {
                sts = twain.DatImageinfo(TWAIN.DG.IMAGE, TWAIN.MSG.GET, ref twimageinfo);
                if (sts != TWAIN.STS.SUCCESS)
                {
                    TWAINWorkingGroup.Log.Error("ReportImage: DatImageinfo failed...");
                    m_blCancel = false;
                    SetImageBlocksDrained(sts);
                    return (TWAINCSToolkit.MSG.RESET);
                }
            }

            // Get the metadata for TW_EXTIMAGEINFO...
            TWAIN.TW_EXTIMAGEINFO twextimageinfo = default(TWAIN.TW_EXTIMAGEINFO);
            TWAIN.TW_INFO twinfo = default(TWAIN.TW_INFO);
            if (m_blExtImageInfo)
            {
                twextimageinfo.NumInfos = 0;
                twinfo.InfoId = (ushort)TWAIN.TWEI.PAGESIDE; twextimageinfo.Set(twextimageinfo.NumInfos++, ref twinfo);
                sts = twain.DatExtimageinfo(TWAIN.DG.IMAGE, TWAIN.MSG.GET, ref twextimageinfo);
                if (sts != TWAIN.STS.SUCCESS)
                {
                    m_blExtImageInfo = false;
                }
            }

            // Make sure we have a folder...
            if (!Directory.Exists(m_szImagesFolder))
            {
                try
                {
                    Directory.CreateDirectory(m_szImagesFolder);
                }
                catch
                {
                    TWAINWorkingGroup.Log.Error("ReportImage: unable to create the image destination directory: " + m_szImagesFolder);
                    m_blCancel = false;
                    SetImageBlocksDrained(TWAIN.STS.FILENOTFOUND);
                    return (TWAINCSToolkit.MSG.RESET);
                }
            }

            // Create a filename...
            m_iImageCount += 1;
            szFile = m_szImagesFolder + Path.DirectorySeparatorChar + "img" + m_iImageCount.ToString("D6");

            // Cleanup...
            if (File.Exists(szFile + ".pdf"))
            {
                try
                {
                    File.Delete(szFile + ".pdf");
                }
                catch
                {
                }
            }

            // Our filename...
            szImageFile = szFile + ".pdf";

            // Get our pixelFormat...
            string szPixelFormat;
            switch ((TWAIN.TWPT)twimageinfo.PixelType)
            {
                default:
                    TWAINWorkingGroup.Log.Error("ReportImage: unable to save the image file <" + szImageFile + ">");
                    m_blCancel = false;
                    SetImageBlocksDrained(TWAIN.STS.FILEWRITEERROR);
                    return (TWAINCSToolkit.MSG.RESET);
                case TWAIN.TWPT.BW:
                    szPixelFormat = "bw1";
                    break;
                case TWAIN.TWPT.GRAY:
                    szPixelFormat = "gray8";
                    break;
                case TWAIN.TWPT.RGB:
                    szPixelFormat = "rgb24";
                    break;
            }

            // Get our compression...
            string szCompression;
            switch ((TWAIN.TWCP)twimageinfo.Compression)
            {
                default:
                    TWAINWorkingGroup.Log.Error("ReportImage: unable to save the image file <" + szImageFile + ">");
                    m_blCancel = false;
                    SetImageBlocksDrained(TWAIN.STS.FILEWRITEERROR);
                    return (TWAINCSToolkit.MSG.RESET);
                case TWAIN.TWCP.NONE:
                    szCompression = "none";
                    break;
                case TWAIN.TWCP.GROUP4:
                    szCompression = "group4";
                    break;
                case TWAIN.TWCP.JPEG:
                    szCompression = "jpeg";
                    break;
            }

            // Save as PDF/Raster...
            blSuccess = PdfRaster.CreatePdfRaster
            (
                szImageFile,
                a_abImage,
                a_iImageOffset,
                szPixelFormat,
                szCompression,
                twimageinfo.XResolution.Whole,
                twimageinfo.ImageWidth,
                twimageinfo.ImageLength
            );
            if (!blSuccess)
            {
                TWAINWorkingGroup.Log.Error("ReportImage: unable to save the image file, " + szImageFile);
                m_blCancel = false;
                SetImageBlocksDrained(TWAIN.STS.FILEWRITEERROR);
                return (TWAINCSToolkit.MSG.RESET);
            }

            // Work out the source...
            string szSource = "";
            if (m_blFlatbed)
            {
                szSource = "flatbed";
            }

            // The image came from a feeder...
            else
            {
                // See if we can get the side from the extended image info...
                if (m_blExtImageInfo)
                {
                    for (uu = 0; uu < twextimageinfo.NumInfos; uu++)
                    {
                        twextimageinfo.Get(uu, ref twinfo);
                        if (twinfo.InfoId == (ushort)TWAIN.TWEI.PAGESIDE)
                        {
                            if (twinfo.ReturnCode == (ushort)TWAIN.STS.SUCCESS)
                            {
                                if (twinfo.Item == (UIntPtr)TWAIN.TWCS.TOP)
                                {
                                    szSource = "feederFront";
                                }
                                else
                                {
                                    szSource = "feederRear";
                                }
                            }
                            break;
                        }
                    }
                }

                // We didn't get a pageside.  So we're going to make
                // the best guess we can.
                if (szSource == "")
                {
                    // We're just doing simplex front at the moment...
                    if (!m_blDuplex)
                    {
                        szSource = "feederFront";
                    }

                    // We're duplex...
                    else
                    {
                        // Odd number images (we start at 1)...
                        if ((m_iImageCount & 1) == 1)
                        {
                            szSource = "feederFront";
                        }
                        // Even number images...
                        else
                        {
                            szSource = "feederRear";
                        }
                    }
                }
            }

            // Try to sort out a lookup...
            ProcessSwordTask.ConfigureNameLookup configurenamelookup = null;
            if (m_configurenamelookup != null)
            {
                configurenamelookup = m_configurenamelookup.Find(szSource, szPixelFormat);
            }
            else
            {
                ProcessSwordTask.ConfigureNameLookup.Add(ref configurenamelookup, "stream0", "source0", "pixelFormat0", "", "");
            }

            // Create the TWAIN Direct metadata...
            string szMeta = "";

            // TWAIN Direct metadata.address begin...
            szMeta += "\"metadata\":{";

            // TWAIN Direct metadata.address begin...
            szMeta += "\"address\":{";

            // Imagecount (counts images)...
            szMeta += "\"imageNumber\":" + m_iImageCount + ",";

            // Segmentcount (long document or huge document)...
            szMeta += "\"imagePart\":" + "1" + ",";

            // Segmentlast (long document or huge document)...
            szMeta += "\"moreParts\":" + "\"lastPartInFile\",";

            // Sheetcount (counts sheets, including ones lost to blank image dropout)...
            szMeta += "\"sheetNumber\":" + "1" + ",";

            // The image came from a flatbed or a feederFront or whatever...
            szMeta += "\"source\":\"" + szSource + "\",";

            // Name of this stream...
            szMeta += "\"streamName\":\"" + configurenamelookup.GetStreamName() + "\",";

            // Name of this source...
            szMeta += "\"sourceName\":\"" + configurenamelookup.GetSourceName() + "\",";

            // Name of this pixelFormat...
            szMeta += "\"pixelFormatName\":\"" + configurenamelookup.GetPixelFormatName() + "\"";

            // TWAIN Direct metadata.address end...
            szMeta += "},";

            // TWAIN Direct metadata.image begin...
            szMeta += "\"image\":{";

            // Add compression...
            szMeta += "\"compression\":\"" + szCompression + "\",";

            // Add pixel format...
            szMeta += "\"pixelFormat\":\"" + szPixelFormat + "\",";

            // Add height...
            szMeta += "\"pixelHeight\":" + twimageinfo.ImageLength + ",";

            // X-offset...
            szMeta += "\"pixelOffsetX\":" + "0" + ",";

            // Y-offset...
            szMeta += "\"pixelOffsetY\":" + "0" + ",";

            // Add width...
            szMeta += "\"pixelWidth\":" + twimageinfo.ImageWidth + ",";

            // Add resolution...
            szMeta += "\"resolution\":" + twimageinfo.XResolution.Whole + ",";

            // Add size...
            FileInfo fileinfo = new FileInfo(szImageFile);
            szMeta += "\"size\":" + fileinfo.Length;

            // TWAIN Direct metadata.image end...
            szMeta += "},";

            // Open SWORD.metadata.status...
            szMeta += "\"status\":{";

            // Add the status...
            szMeta += "\"success\":true";

            // TWAIN Direct metadata.status end...
            szMeta += "}";

            // TWAIN Direct metadata end...
            szMeta += "}";

            // Save the metadata to disk...
            try
            {
                File.WriteAllText(szFile + ".meta", szMeta);
                TWAINWorkingGroup.Log.Info("ReportImage: saved " + szFile + ".meta");
            }
            catch
            {
                TWAINWorkingGroup.Log.Error("ReportImage: unable to save the metadata file...");
                m_blCancel = false;
                SetImageBlocksDrained(TWAIN.STS.FILEWRITEERROR);
                return (TWAINCSToolkit.MSG.RESET);
            }

            // We've been asked to cancel, so sneak that in...
            if (m_blCancel)
            {
                TWAIN.TW_PENDINGXFERS twpendingxfers = default(TWAIN.TW_PENDINGXFERS);
                sts = twain.DatPendingxfers(TWAIN.DG.CONTROL, TWAIN.MSG.STOPFEEDER, ref twpendingxfers);
                if (sts != TWAIN.STS.SUCCESS)
                {
                    TWAINWorkingGroup.Log.Error("ReportImage: DatPendingxfers failed...");
                    m_blCancel = false;
                    SetImageBlocksDrained(sts);
                    return (TWAINCSToolkit.MSG.STOPFEEDER);
                }
            }

            // All done...
            return (TWAINCSToolkit.MSG.ENDXFER);
        }

        /// <summary>
        /// Remove the imageBlocksDrained file...
        /// </summary>
        private void ClearImageBlocksDrained()
        {
            m_blSessionImageBlocksDrained = false;
            string szSessionImageBlocksDrained = Path.Combine(m_szImagesFolder, "imageBlocksDrained.meta");
            if (File.Exists(szSessionImageBlocksDrained))
            {
                File.Delete(szSessionImageBlocksDrained);
            }
        }

        /// <summary>
        /// Set imageblocks drained with a status...
        /// </summary>
        /// <param name="a_sts">status of end of job</param>
        private void SetImageBlocksDrained(TWAIN.STS a_sts)
        {
            string szSessionImageBlocksDrained = Path.Combine(m_szImagesFolder, "imageBlocksDrained.meta");
            if (!File.Exists(szSessionImageBlocksDrained))
            {
                TWAINWorkingGroup.Log.Info("SetImageBlocksDrained: " + a_sts);
                try
                {
                    File.WriteAllText
                    (
                        szSessionImageBlocksDrained,
                        "{" +
                        "\"detected\":\"" + a_sts + "\"" +
                        "}"
                    );
                }
                catch (Exception exception)
                { 
                    TWAINWorkingGroup.Log.Error("SetImageBlocksDrained: error writing <" + szSessionImageBlocksDrained + "> - " + exception.Message);
                }
            }
        }

        #endregion


        // Private Methods: TWAIN Direct Client-Scanner API
        #region Private Methods: TWAIN Direct Client-Scanner API

        // The naming convention for this bit is Executer / Package / Command.  So, since
        // this is the device (scanner) section, the executer is the Device.  The TWAIN Local
        // package is "scanner" and the commands are TWAIN Direct Client-Scanner API commands.  If you
        // want to find the corresponding function used by applications, just replace
        // "Device" with "Client"...

        /// <summary>
        /// Close the TWAIN driver...
        /// </summary>
        /// <param name="a_szSession">the session data</param>
        /// <returns>a twain local status</returns>
        private TwainLocalScanner.ApiStatus DeviceScannerCloseSession(out string a_szSession)
        {
            string szStatus;
            string szPendingxfers;
            string szUserinterface;

            // Init stuff...
            a_szSession = "";

            // Validate...
            if ((m_twaincstoolkit == null) || (m_szTwainDriverIdentity == null))
            {
                return (TwainLocalScanner.ApiStatus.invalidSessionId);
            }

            // Build the reply (we need this before the close so that we can get
            // the image block info, if there is any)...
            DeviceScannerGetSession(out a_szSession);

            // If we're out of images, then bail...
            if (m_blSessionImageBlocksDrained)
            {
                // Close the driver...
                szStatus = "";
                m_twaincstoolkit.Send("DG_CONTROL", "DAT_IDENTITY", "MSG_CLOSEDS", ref m_szTwainDriverIdentity, ref szStatus);
                m_twaincstoolkit.Cleanup();
                m_twaincstoolkit = null;
                m_szTwainDriverIdentity = null;
                return (TwainLocalScanner.ApiStatus.success);
            }

            // Otherwise, just make sure we've stopped scanning...
            switch (this.m_twaincstoolkit.GetState())
            {
                // DG_CONTROL / DAT_PENDINGXFERS / MSG_ENDXFER...
                case 7:
                    szStatus = "";
                    szPendingxfers = "0,0";
                    m_twaincstoolkit.Send("DG_CONTROL", "DAT_PENDINGXFERS", "MSG_ENDXFER", ref szPendingxfers, ref szStatus);
                    break;

                // DG_CONTROL / DAT_PENDINGXFERS / MSG_RESET...
                case 6:
                    szStatus = "";
                    szPendingxfers = "0,0";
                    m_twaincstoolkit.Send("DG_CONTROL", "DAT_PENDINGXFERS", "MSG_RESET", ref szPendingxfers, ref szStatus);
                    break;

                // DG_CONTROL / DAT_USERINTERFACE / MSG_DISABLEDS, but only if we have no images...
                case 5:
                    szStatus = "";
                    szUserinterface = "0,0";
                    m_twaincstoolkit.Send("DG_CONTROL", "DAT_USERINTERFACE", "MSG_DISABLEDS", ref szUserinterface, ref szStatus);
                    break;
            }

            // All done...
            return (TwainLocalScanner.ApiStatus.success);
        }

        /// <summary>
        /// Open the TWAIN driver...
        /// </summary>
        /// <param name="a_jsonlookup">data for the open</param>
        /// <param name="a_szSession">the session data</param>
        /// <returns>a twain local status</returns>
        private TwainLocalScanner.ApiStatus DeviceScannerCreateSession(JsonLookup a_jsonlookup, out string a_szSession)
        {
            string szStatus;
            TWAIN.STS sts;

            // Init stuff...
            a_szSession = "";

            // Make sure the images folder is empty...
            if (Directory.Exists(m_szImagesFolder))
            {
                Directory.Delete(m_szImagesFolder, true);
            }
            Directory.CreateDirectory(m_szImagesFolder);

            // Create the toolkit...
            try
            {
                m_twaincstoolkit = new TWAINCSToolkit
                (
                    m_intptrHwnd,
                    null,
                    ReportImage,
                    null,
                    "TWAIN Working Group",
                    "TWAIN Sharp",
                    "SWORD-on-TWAIN",
                    2,
                    3,
                    new string[] { "DF_APP2", "DG_CONTROL", "DG_IMAGE" },
                    "USA",
                    "testing...",
                    "ENGLISH_USA",
                    1,
                    0,
                    false,
                    true,
                    m_runinuithreaddelegate,
                    m_objectRunInUiThread
                );
            }
            catch
            {
                m_twaincstoolkit = null;
                m_szTwainDriverIdentity = null;
                return (TwainLocalScanner.ApiStatus.newSessionNotAllowed);
            }

            // Load our deviceregister object...
            m_deviceregisterSession = new DeviceRegister();
            m_deviceregisterSession.Load("{\"scanner\":" + a_jsonlookup.Get("scanner") + "}");

            // Life sucks.
            // On a side note, the ty= field contains the TW_IDENTITY.ProductName
            // we need to find our scanner...
            if (TwainLocalScanner.GetPlatform() == TwainLocalScanner.Platform.WINDOWS)
            {
                m_szTwainDriverIdentity = "0,0,0,USA,USA, ,0,0,0xFFFFFFFF, , ," + m_deviceregisterSession.GetTwainLocalTy();
            }
            else if (TwainLocalScanner.GetPlatform() == TwainLocalScanner.Platform.MACOSX)
            {
                m_szTwainDriverIdentity = "0,0,0,USA,USA, ,0,0,0xFFFFFFFF, , ," + m_deviceregisterSession.GetTwainLocalTy();
            }
            else
            {
                m_szTwainDriverIdentity = "1,0,0,USA,USA, ,0,0,0xFFFFFFFF, , ," + m_deviceregisterSession.GetTwainLocalTy();
            }

            // Open the driver...
            szStatus = "";
            sts = m_twaincstoolkit.Send("DG_CONTROL", "DAT_IDENTITY", "MSG_OPENDS", ref m_szTwainDriverIdentity, ref szStatus);
            if (sts != TWAIN.STS.SUCCESS)
            {
                return (TwainLocalScanner.ApiStatus.newSessionNotAllowed);
            }

            // Build the reply...
            DeviceScannerGetSession(out a_szSession);

            // All done...
            return (TwainLocalScanner.ApiStatus.success);
        }

        /// <summary>
        /// Get the session data...
        /// </summary>
        /// <param name="a_szSession">the session data</param>
        /// <returns>status of the call</returns>
        private TwainLocalScanner.ApiStatus DeviceScannerGetSession(out string a_szSession)
        {
            string[] aszFiles;

            // Init stuff...
            a_szSession = "";

            // Validate...
            if ((m_twaincstoolkit == null) || (m_szTwainDriverIdentity == null))
            {
                return (TwainLocalScanner.ApiStatus.invalidSessionId);
            }

            // Look for images, the nice thing about this is that we don't
            // have to worry about our scanning state.  If we are scanning
            // and we have images, then we'll report them...
            try
            {
                aszFiles = Directory.GetFiles(m_szImagesFolder, "img*.meta");
            }
            catch
            {
                aszFiles = null;
            }
            string szImageBlocks = "";
            if (aszFiles != null)
            {
                foreach (string szFile in aszFiles)
                {
                    // We write the meta after the pdf, so if we have this we have the other...
                    if (szFile.EndsWith(".meta"))
                    {
                        string sz = Path.GetFileNameWithoutExtension(szFile);
                        sz = sz.Replace("img", "");
                        int iNumber = int.Parse(sz);
                        szImageBlocks += ((szImageBlocks != "") ? "," : "") + iNumber.ToString();
                    }
                }
            }

            // If we have no images, then check if the the scanner says
            // that we're out of images...
            if (string.IsNullOrEmpty(szImageBlocks) && File.Exists(Path.Combine(m_szImagesFolder, "imageBlocksDrained.meta")))
            {
                string szReason = File.ReadAllText(Path.Combine(m_szImagesFolder, "imageBlocksDrained.meta"));
                TWAINWorkingGroup.Log.Info("imageBlocksDrained.meta: " + szReason);
                m_blSessionImageBlocksDrained = true;
            }

            // Build the reply.  Note that we have this kind of code in three places
            // in the solution.  This is the lowest "level", where we generate the
            // data that will be sent to TwainDirect.Scanner, so it's not really in
            // the final form, though it's close.
            a_szSession = "\"session\":{";

            // Tack on the image blocks, if we have any...
            if (!string.IsNullOrEmpty(szImageBlocks))
            {
                a_szSession += "\"imageBlocks\":[" + szImageBlocks + "]";
            }

            // End of the session object...
            a_szSession += "}";

            // All done...
            return (TwainLocalScanner.ApiStatus.success);
        }

        /// <summary>
        /// Return the full path to the requested image block, we also get the
        /// file to the metadata, but the caller decides if we send this back
        /// or not based on the value of "withMetadata"...
        /// </summary>
        /// <param name="a_jsonlookup">data for the open</param>
        /// <param name="a_szImageFile">file containing the image data</param>
        /// <param name="a_szMetadataFile">file containing the metadata</param>
        /// <returns>status of the call</returns>
        private TwainLocalScanner.ApiStatus DeviceScannerReadImageBlock
        (
            JsonLookup a_jsonlookup,
            out string a_szImageFile,
            out string a_szMetadataFile
        )
        {
            // Build the filename...
            int iImageBlock = int.Parse(a_jsonlookup.Get("imageBlockNum"));
            a_szImageFile = Path.Combine(m_szImagesFolder, "img" + iImageBlock.ToString("D6"));
            a_szImageFile = a_szImageFile.Replace("\\", "/");
            if (File.Exists(a_szImageFile + ".pdf"))
            {
                a_szImageFile += ".pdf";
            }
            else
            {
                TWAINWorkingGroup.Log.Error("Image not found: " + a_szImageFile);
                a_szMetadataFile = "";
                return (TwainLocalScanner.ApiStatus.invalidImageBlockNumber);
            }

            // Build the metadata filename, if we don't have one, we have a problem...
            a_szMetadataFile = Path.Combine(m_szImagesFolder, "img" + iImageBlock.ToString("D6") + ".meta");
            a_szMetadataFile = a_szMetadataFile.Replace("\\", "/");
            if (!File.Exists(a_szMetadataFile))
            {
                TWAINWorkingGroup.Log.Error("Image metadata not found: " + a_szMetadataFile);
                a_szMetadataFile = "";
                return (TwainLocalScanner.ApiStatus.invalidImageBlockNumber);
            }

            // All done...
            return (TwainLocalScanner.ApiStatus.success);
        }

        /// <summary>
        /// Return the TWAIN Direct metadata for this image block, note that we
        /// generate the metadata file last, because it's the trigger that says
        /// that this image is complete...
        /// </summary>
        /// <param name="a_jsonlookup">data for the open</param>
        /// <param name="a_szMetadataFile">file containing the metadata</param>
        /// <param name="a_szThumbnailFile">optional file containing the thumbnail</param>
        /// <returnsstatus of the call</returns>
        private bool ThumbnailCallback()
        {
            return false;
        }
        private TwainLocalScanner.ApiStatus DeviceScannerReadImageBlockMetadata
        (
            JsonLookup a_jsonlookup,
            out string a_szMetadataFile,
            out string a_szThumbnailFile
        )
        {
            int iImageBlock;
            string szPdf;

            // Get our imageblock number...
            iImageBlock = int.Parse(a_jsonlookup.Get("imageBlockNum"));

            // Generate a thumbnail...
            a_szThumbnailFile = "";
            if (a_jsonlookup.Get("withThumbnail") == "true")
            {
                bool blSuccess;

                // The name of our image file...
                szPdf = Path.Combine(m_szImagesFolder, "img" + iImageBlock.ToString("D6") + ".pdf");
                szPdf = szPdf.Replace("\\", "/");

                // This is the file we'll use...
                a_szThumbnailFile = Path.Combine(m_szImagesFolder, "img" + iImageBlock.ToString("D6") + "_thumbnail.pdf");
                a_szThumbnailFile = a_szThumbnailFile.Replace("\\", "/");

                // Create the thumbnail...
                blSuccess = PdfRaster.CreatePdfRasterThumbnail(szPdf, a_szThumbnailFile);
            }

            // Build the metadata filename, if we don't have one, we have a problem...
            a_szMetadataFile = Path.Combine(m_szImagesFolder, "img" + iImageBlock.ToString("D6") + ".meta");
            a_szMetadataFile = a_szMetadataFile.Replace("\\", "/");
            if (!File.Exists(a_szMetadataFile))
            {
                TWAINWorkingGroup.Log.Error("Image metadata not found: " + a_szMetadataFile);
                a_szMetadataFile = "";
                a_szThumbnailFile = "";
                return (TwainLocalScanner.ApiStatus.invalidImageBlockNumber);
            }

            // All done...
            return (TwainLocalScanner.ApiStatus.success);
        }

        /// <summary>
        /// Release image blocks.  Compare what the user asks to release
        /// to what we really have, or we could be here for a while...
        /// </summary>
        /// <param name="a_jsonlookup">data for the command</param>
        /// <param name="a_szSession">the session data</param>
        /// <returns>a twain local status</returns>
        private TwainLocalScanner.ApiStatus DeviceScannerReleaseImageBlocks(JsonLookup a_jsonlookup, out string a_szSession)
        {
            int ii;
            int iImageBlockNum;
            int iLastImageBlockNum;
            int iImageBlockNumFile;
            int iLastImageBlockNumFile;
            string szFile;
            string szNumber;
            string[] aszFiles;

            // Init stuff...
            a_szSession = "";

            // Get the endpoints (inclusive)...
            iImageBlockNum = int.Parse(a_jsonlookup.Get("imageBlockNum"));
            iLastImageBlockNum = int.Parse(a_jsonlookup.Get("lastImageBlockNum"));

            // Get the files...
            aszFiles = Directory.GetFiles(m_szImagesFolder, "img*.pdf");

            // If we have no files, build the reply and return...
            if ((aszFiles == null) || (aszFiles.Length == 0))
            {
                DeviceScannerGetSession(out a_szSession);
                return (TwainLocalScanner.ApiStatus.success);
            }

            // Make sure the list is sorted...
            Array.Sort(aszFiles);

            // Get the number for the first file we found...
            szNumber = aszFiles[0].Substring(Path.Combine(m_szImagesFolder, "img").Length, 6);
            if (!int.TryParse(szNumber, out iImageBlockNumFile))
            {
                iImageBlockNumFile = iImageBlockNum;
            }

            // Get the number for the last file we found...
            szNumber = aszFiles[aszFiles.Length - 1].Substring(Path.Combine(m_szImagesFolder, "img").Length, 6);
            if (!int.TryParse(szNumber, out iLastImageBlockNumFile))
            {
                iLastImageBlockNumFile = iLastImageBlockNum;
            }

            // Pin the caller's numbers to what we really have...
            if (iImageBlockNum < iImageBlockNumFile)
            {
                iImageBlockNum = iImageBlockNumFile;
            }
            if (iLastImageBlockNum > iLastImageBlockNumFile)
            {
                iLastImageBlockNum = iLastImageBlockNumFile;
            }

            // Loopy...
            for (ii = iImageBlockNum; ii <= iLastImageBlockNum; ii++)
            {
                // Build the filename...
                szFile = Path.Combine(m_szImagesFolder, "img" + ii.ToString("D6"));
                if (File.Exists(szFile + ".meta"))
                {
                    try
                    {
                        File.Delete(szFile + ".meta");
                    }
                    catch
                    {
                        // We don't care if this fails...
                    }
                }
                if (File.Exists(szFile + ".txt"))
                {
                    try
                    {
                        File.Delete(szFile + ".txt");
                    }
                    catch
                    {
                        // We don't care if this fails...
                    }
                }
                if (File.Exists(szFile + ".pdf"))
                {
                    try
                    {
                        File.Delete(szFile + ".pdf");
                    }
                    catch
                    {
                        // We don't care if this fails...
                    }
                }
            }

            // Build the reply...
            DeviceScannerGetSession(out a_szSession);

            // All done...
            return (TwainLocalScanner.ApiStatus.success);
        }

        /// <summary>
        /// Process a TWAIN Direct task...
        /// </summary>
        /// <param name="a_jsonlookup">data for the task</param>
        /// <param name="a_swordtask">the result of the task</param>
        /// <param name="a_blSetAppCapabilities">set the application capabilities (ex: ICAP_XFERMECH)</param>
        /// <returns>a twain local status</returns>
        private TwainLocalScanner.ApiStatus DeviceScannerSendTask(JsonLookup a_jsonlookup, out ProcessSwordTask a_processswordtask, ref bool a_blSetAppCapabilities)
        {
            bool blSuccess;
            string szTask;
            string szStatus;
            TWAIN.STS sts;

            // Init stuff...
            a_processswordtask = new ProcessSwordTask(m_szImagesFolder, m_twaincstoolkit, m_deviceregisterSession);

            // Get the task from the TWAIN Local command...
            szTask = a_jsonlookup.GetJson("task");

            // TWAIN Driver Support...
            #region TWAIN Driver Support

            // Have the driver process the task...
            if (m_deviceregisterSession.GetTwainLocalTwainDirectSupport() == DeviceRegister.TwainDirectSupport.Driver)
            {
                string szMetadata;
                TWAIN.TW_TWAINDIRECT twtwaindirect = default(TWAIN.TW_TWAINDIRECT);

                // Convert the task to an array, and then copy it into
                // memory pointed to by a handle.  I'm NUL terminating
                // the data because it feels safer that way...
                byte[] abTask = Encoding.UTF8.GetBytes(szTask);
                IntPtr intptrTask = Marshal.AllocHGlobal(abTask.Length + 1);
                Marshal.Copy(abTask, 0, intptrTask, abTask.Length);
                Marshal.WriteByte(intptrTask, abTask.Length, 0);

                // Build the command...
                szMetadata =
                    Marshal.SizeOf(twtwaindirect) + "," +   // SizeOf
                    "0" + "," +                             // CommunicationManager
                    intptrTask + "," +                      // Send
                    abTask.Length + "," +                   // SendSize
                    "0" + "," +                             // Receive
                    "0";                                    // ReceiveSize

                // Send the command...
                szStatus = "";
                sts = m_twaincstoolkit.Send("DG_CONTROL", "DAT_TWAINDIRECT", "MSG_SETTASK", ref szMetadata, ref szStatus);
                if (sts != TWAIN.STS.SUCCESS)
                {
                    TWAINWorkingGroup.Log.Error("Process: MSG_SENDTASK failed");
                    Marshal.FreeHGlobal(intptrTask);
                    intptrTask = IntPtr.Zero;
                    //m_swordtaskresponse.SetError("fail", null, "invalidJson", lResponseCharacterOffset);
                    return (TwainLocalScanner.ApiStatus.invalidCapturingOptions);
                }

                // TBD: Open up the reply (we should probably get the CsvToTwaindirect
                // function to do this for us)...
                string[] asz = szMetadata.Split(new char[] { ',' });
                if ((asz == null) || (asz.Length < 6))
                {
                    TWAINWorkingGroup.Log.Error("Process: MSG_SENDTASK failed");
                    Marshal.FreeHGlobal(intptrTask);
                    intptrTask = IntPtr.Zero;
                    //m_swordtaskresponse.SetError("fail", null, "invalidJson", lResponseCharacterOffset);
                    return (TwainLocalScanner.ApiStatus.invalidCapturingOptions);
                }

                // Get the reply data...
                long lReceive;
                if (!long.TryParse(asz[4], out lReceive) || (lReceive == 0))
                {
                    TWAINWorkingGroup.Log.Error("Process: MSG_SENDTASK failed");
                    Marshal.FreeHGlobal(intptrTask);
                    intptrTask = IntPtr.Zero;
                    return (TwainLocalScanner.ApiStatus.invalidCapturingOptions);
                }
                IntPtr intptrReceiveHandle = new IntPtr(lReceive);
                uint u32ReceiveBytes;
                if (!uint.TryParse(asz[5], out u32ReceiveBytes) || (u32ReceiveBytes == 0))
                {
                    TWAINWorkingGroup.Log.Error("Process: MSG_SENDTASK failed");
                    m_twaincstoolkit.DsmMemFree(ref intptrReceiveHandle);
                    Marshal.FreeHGlobal(intptrTask);
                    intptrTask = IntPtr.Zero;
                    //m_swordtaskresponse.SetError("fail", null, "invalidJson", lResponseCharacterOffset);
                    return (TwainLocalScanner.ApiStatus.invalidCapturingOptions);
                }

                // Convert it to an array and then a string...
                IntPtr intptrReceive = m_twaincstoolkit.DsmMemLock(intptrReceiveHandle);
                byte[] abReceive = new byte[u32ReceiveBytes];
                Marshal.Copy(intptrReceive, abReceive, 0, (int)u32ReceiveBytes);
                string szReceive = Encoding.UTF8.GetString(abReceive);
                m_twaincstoolkit.DsmMemUnlock(intptrReceiveHandle);

                // Cleanup...
                m_twaincstoolkit.DsmMemFree(ref intptrReceiveHandle);
                Marshal.FreeHGlobal(intptrTask);
                intptrTask = IntPtr.Zero;

                // Squirrel the reply away...
                a_processswordtask.SetTaskReply(szReceive);
                return (TwainLocalScanner.ApiStatus.success);
            }

            #endregion

            // TWAIN Bridge Support...
            #region TWAIN Bridge Support...

            // Deserialize our task...
            blSuccess = a_processswordtask.Deserialize(szTask, "211a1e90-11e1-11e5-9493-1697f925ec7b");
            if (!blSuccess)
            {
                return (TwainLocalScanner.ApiStatus.invalidCapturingOptions);
            }

            // Process our task...
            blSuccess = a_processswordtask.ProcessAndRun(out m_configurenamelookup);
            if (!blSuccess)
            {
                return (TwainLocalScanner.ApiStatus.invalidCapturingOptions);
            }

            #endregion

            // All done...
            return (TwainLocalScanner.ApiStatus.success);
        }

        /// <summary>
        /// Start scanning...
        /// </summary>
        /// <param name="a_szSession">the session data</param>
        /// <returns>a twain local status</returns>
        private TwainLocalScanner.ApiStatus DeviceScannerStartCapturing(ref bool a_blSetAppCapabilities, out string a_szSession)
        {
            string szStatus;
            string szCapability;
            string szUserInterface;
            TWAIN.STS sts;

            // Init stuff...
            m_blCancel = false;
            m_iImageCount = 0;
            a_szSession = "";
            ClearImageBlocksDrained();

            // Validate...
            if (m_twaincstoolkit == null)
            {
                return (TwainLocalScanner.ApiStatus.invalidSessionId);
            }

            // Only do this if we haven't done it already...
            if (!a_blSetAppCapabilities)
            {
                // We should only have to do it once...
                a_blSetAppCapabilities = true;

                // Memory transfer...
                szStatus = "";
                szCapability = "ICAP_XFERMECH,TWON_ONEVALUE,TWTY_UINT16,2";
                sts = m_twaincstoolkit.Send("DG_CONTROL", "DAT_CAPABILITY", "MSG_SET", ref szCapability, ref szStatus);
                if (sts != TWAIN.STS.SUCCESS)
                {
                    TWAINWorkingGroup.Log.Info("Action: we can't set ICAP_XFERMECH to TWSX_MEMORY");
                    return (TwainLocalScanner.ApiStatus.invalidCapturingOptions);
                }

                // No UI...
                szStatus = "";
                szCapability = "CAP_INDICATORS,TWON_ONEVALUE,TWTY_BOOL,0";
                sts = m_twaincstoolkit.Send("DG_CONTROL", "DAT_CAPABILITY", "MSG_SET", ref szCapability, ref szStatus);
                if (sts != TWAIN.STS.SUCCESS)
                {
                    TWAINWorkingGroup.Log.Error("Action: we can't set CAP_INDICATORS to FALSE");
                    return (TwainLocalScanner.ApiStatus.invalidCapturingOptions);
                }

                // Ask for extended image info...
                m_blExtImageInfo = true;
                szStatus = "";
                szCapability = "ICAP_EXTIMAGEINFO,TWON_ONEVALUE,TWTY_BOOL,1";
                sts = m_twaincstoolkit.Send("DG_CONTROL", "DAT_CAPABILITY", "MSG_SET", ref szCapability, ref szStatus);
                if (sts != TWAIN.STS.SUCCESS)
                {
                    TWAINWorkingGroup.Log.Warn("Action: we can't set ICAP_EXTIMAGEINFO to TRUE");
                    m_blExtImageInfo = false;
                }
            }

            // Start scanning (no UI)...
            szStatus = "";
            szUserInterface = "0,0";
            sts = m_twaincstoolkit.Send("DG_CONTROL", "DAT_USERINTERFACE", "MSG_ENABLEDS", ref szUserInterface, ref szStatus);
            if (sts != TWAIN.STS.SUCCESS)
            {
                TWAINWorkingGroup.Log.Info("Action: MSG_ENABLEDS failed");
                return (TwainLocalScanner.ApiStatus.invalidCapturingOptions);
            }

            // Build the reply...
            DeviceScannerGetSession(out a_szSession);

            // All done...
            if (sts == TWAIN.STS.SUCCESS)
            {
                return (TwainLocalScanner.ApiStatus.success);
            }
            return (TwainLocalScanner.ApiStatus.invalidCapturingOptions);
        }

        /// <summary>
        /// Stop the scanner.  We'll try it the nice way first.  If that doesn't fly, then
        /// we'll set a flag to reset the scanner next time we hit a msg_endxfer...
        /// </summary>
        /// <param name="a_szSession">the session data</param>
        /// <returns>a twain local status</returns>
        private TwainLocalScanner.ApiStatus DeviceScannerStopCapturing(out string a_szSession)
        {
            string szStatus;
            string szUserinterface;
            string szPendingxfers;
            TWAIN.STS sts;

            // Init stuff...
            a_szSession = "";

            // Validate...
            if (m_twaincstoolkit == null)
            {
                return (TwainLocalScanner.ApiStatus.invalidSessionId);
            }

            // It looks like we're done, so declare success and scoot...
            if (m_twaincstoolkit.GetState() <= 4)
            {
                sts = TWAIN.STS.SUCCESS;
            }

            // We never got to state 6, this can happen if the request to
            // stopCapturing comes in before we're processed MSG_XFERREADY...
            else if (m_twaincstoolkit.GetState() == 5)
            {
                szStatus = "";
                szUserinterface = "0,0";
                sts = m_twaincstoolkit.Send("DG_CONTROL", "DAT_USERINTERFACE", "MSG_DISABLEDS", ref szUserinterface, ref szStatus);
            }

            // We're done scanning, so bail...
            else if (m_blSessionImageBlocksDrained)
            {
                sts = TWAIN.STS.SUCCESS;
                if (m_twaincstoolkit.GetState() == 5)
                {
                    szStatus = "";
                    szUserinterface = "0,0";
                    sts = m_twaincstoolkit.Send("DG_CONTROL", "DAT_USERINTERFACE", "MSG_DISABLEDS", ref szUserinterface, ref szStatus);
                }
            }

            // We're still scanning, try to end gracefully...
            else
            {
                szStatus = "";
                szPendingxfers = "0,0";
                sts = m_twaincstoolkit.Send("DG_CONTROL", "DAT_PENDINGXFERS", "MSG_STOPFEEDER", ref szPendingxfers, ref szStatus);

                // That didn't go well, then end abruptly...
                if (sts != TWAIN.STS.SUCCESS)
                {
                    szStatus = "";
                    szPendingxfers = "0,0";
                    sts = m_twaincstoolkit.Send("DG_CONTROL", "DAT_PENDINGXFERS", "MSG_RESET", ref szPendingxfers, ref szStatus);
                }
            }

            // Build the reply...
            DeviceScannerGetSession(out a_szSession);

            // All done...
            if (sts == TWAIN.STS.SUCCESS)
            {
                return (TwainLocalScanner.ApiStatus.success);
            }

            // Oh well, we'll try to abort...
            m_blCancel = true;
            return (TwainLocalScanner.ApiStatus.success);
        }

        #endregion


        // Private Attributes...
        #region Private Attributes...

        /// <summary>
        /// The TWAIN Toolkit object that front ends TWAIN for us...
        /// </summary>
        private TWAINCSToolkit m_twaincstoolkit;

        /// <summary>
        /// Information about the scanner sent to use by createSession...
        /// </summary>
        private DeviceRegister m_deviceregisterSession;

        /// <summary>
        /// TWAIN identity of the scanner we're using...
        /// </summary>
        private string m_szTwainDriverIdentity;

        /// <summary>
        /// The folder where we write stuff...
        /// </summary>
        private string m_szWriteFolder;

        /// <summary>
        /// The folder under the writer folder where we keep images...
        /// </summary>
        private string m_szImagesFolder;

        /// <summary>
        /// The path to our interprocess communication files...
        /// </summary>
        private string m_szIpc;

        /// <summary>
        /// Process id we're communicating with...
        /// </summary>
        private int m_iPid;

        /// <summary>
        /// A flag to help us abort a scan with MSG_RESET when
        /// MSG_STOPFEEDER fails to work...
        /// </summary>
        private bool m_blCancel;

        /// <summary>
        /// True if we have support for DAT_EXTIMAGEINFO...
        /// </summary>
        private bool m_blExtImageInfo;

        /// <summary>
        /// Count of images for each TwainStartCapturing call, the
        /// first image is always 1...
        /// </summary>
        private int m_iImageCount;

        /// <summary>
        /// End of job detected...
        /// </summary>
        private bool m_blSessionImageBlocksDrained;

        /// <summary>
        /// We're scanning from a flatbed...
        /// </summary>
        private bool m_blFlatbed;

        /// <summary>
        /// We're scanning duplex (front and rear) off an automatic document feeder (ADF)...
        /// </summary>
        private bool m_blDuplex;

        /// <summary>
        /// The delegate that lets us run stuff in the main GUI thread on Windows,
        /// and some anonymous data that is sent along with it.  We're also holding
        /// onto the handle for the anonymous data...
        /// </summary>
        private TWAINCSToolkit.RunInUiThreadDelegate m_runinuithreaddelegate;
        private object m_objectRunInUiThread;
        private IntPtr m_intptrHwnd;

        /// <summary>
        /// We'll use this to get the stream, source, and pixelFormat names for the
        /// metadata...
        /// </summary>
        ProcessSwordTask.ConfigureNameLookup m_configurenamelookup;

        #endregion
    }
}
