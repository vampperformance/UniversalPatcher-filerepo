﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using static upatcher;
using System.IO;
using System.Windows.Forms;
using System.Diagnostics;
using System.Runtime.Remoting.Messaging;
using System.Management.Instrumentation;

namespace UniversalPatcher
{
    public class DtcSearchConfig
    {
        public DtcSearchConfig()
        {
            MilTable = "opcode";
            CodeOffset = 1;
            CodeSteps = 4;
            StatusOffset = 1;
            MilOffset = 1;
            StatusSteps = 1;
            MilSteps = 1;
        }
        public string XMLFile { get; set; }
        public string CodeSearch { get; set; }
        public string StatusSearch { get; set; }
        public string MilSearch { get; set; }
        public string MilTable { get; set; }
        public int CodeOffset { get; set; }
        public int CodeSteps { get; set; }
        public int StatusOffset { get; set; }
        public int MilOffset { get; set; }
        public int StatusSteps { get; set; }
        public int MilSteps { get; set; }
    }

    public class DtcSearch
    {
        public DtcSearch()
        {

        }


        private int searchStringAddressOffset(string searchStr)
        {
            //searchBytes returns address of first byte, we want address of first *, or end of string
            int offset = 0;

            string[] sParts = searchStr.Trim().Split(' ');
            if (!searchStr.Contains("*"))
                return sParts.Length;

            for (int p = 0; p < sParts.Length; p++)
            {
                if (sParts[p] == "*")
                    return p;
            }

            return offset;
        }

        private string decodeDTC(string code)
        {
            if (code.StartsWith("5"))
            {
                return "U0" + code.Substring(1);
            }
            else if (code.StartsWith("C"))
            {
                return "C0" + code.Substring(1);
            }
            else if (code.StartsWith("D"))
            {
                return "U1" + code.Substring(1);
            }
            else if (code.StartsWith("E"))
            {
                return "U2" + code.Substring(1);
            }
            else
            {
                return "P" + code;
            }

        }
        public string searchDtc(PcmFile PCM)
        {
            try
            {
                string OBD2CodeFile = Path.Combine(Application.StartupPath, "XML", "OBD2Codes.xml");
                if (File.Exists(OBD2CodeFile))
                {
                    Debug.WriteLine("Loading OBD2Codes.xml");
                    System.Xml.Serialization.XmlSerializer reader = new System.Xml.Serialization.XmlSerializer(typeof(List<OBD2Code>));
                    System.IO.StreamReader file = new System.IO.StreamReader(OBD2CodeFile);
                    OBD2Codes = (List<OBD2Code>)reader.Deserialize(file);
                    file.Close();
                }
                else
                {
                    OBD2Codes = new List<OBD2Code>();
                }

                //Search DTC codes:
                uint opCodeAddr = 0;
                uint codeAddr = 0;
                string searchStr;
                int configIndex = 0;
                dtcCombined = false;

                for (configIndex = 0; configIndex < dtcSearchConfigs.Count; configIndex++)
                {
                    if (PCM.xmlFile == dtcSearchConfigs[configIndex].XMLFile.ToLower())
                    {
                        searchStr = dtcSearchConfigs[configIndex].CodeSearch;
                        opCodeAddr = searchBytes(PCM, searchStr, 0, PCM.fsize);
                        //Check if we found status table, too:
                        uint tmpAddr = searchBytes(PCM, dtcSearchConfigs[configIndex].StatusSearch, 0, PCM.fsize);
                        if (opCodeAddr < uint.MaxValue && tmpAddr < uint.MaxValue)
                        {
                            codeAddr = BEToUint32(PCM.buf, opCodeAddr + (uint)searchStringAddressOffset(searchStr));
                            Debug.WriteLine("Code search string: " + searchStr);
                            Debug.WriteLine("DTC code table address: " + codeAddr.ToString("X") + ", opcodeaddress: " + opCodeAddr.ToString("X"));
                            codeAddr += (uint)dtcSearchConfigs[configIndex].CodeOffset;
                            break;
                        }
                    }
                }

                if (codeAddr == 0)
                {
                    if (PCM.xmlFile == "e38" || PCM.xmlFile == "e67")
                    {
                        dtcCombined = true;
                        string retval = SearchDtcE38(PCM);
                        return retval;
                    }
                    return "DTC search: can't find DTC code table";
                }

                //Read codes:
                bool dCodes = false;
                for (uint addr = codeAddr; addr < PCM.fsize; addr += (uint)dtcSearchConfigs[configIndex].CodeSteps)
                {
                    dtcCode dtc = new dtcCode();
                    dtc.codeAddrInt = addr;
                    dtc.CodeAddr = addr.ToString("X8");
                    dtc.codeInt = BEToUint16(PCM.buf, addr);

                    string codeTmp = dtc.codeInt.ToString("X");
                    if (dCodes && !codeTmp.StartsWith("D") || (dtc.codeInt < 10 && dtcCodes.Count > 10))
                    {
                        break;
                    }
                    codeTmp = dtc.codeInt.ToString("X4");
                    dtc.Code = decodeDTC(codeTmp);
                    if (codeTmp.StartsWith("D")) dCodes = true;
                    //Find description for code:
                    for (int o = 0; o < OBD2Codes.Count; o++)
                    {
                        if (dtc.Code == OBD2Codes[o].Code)
                        {
                            dtc.Description = OBD2Codes[o].Description;
                            break;
                        }
                    }
                    dtcCodes.Add(dtc);
                }

                //Search Code status table:
                uint statusAddr = uint.MaxValue;
                uint milAddr = uint.MaxValue;
                List<uint> milAddrList = new List<uint>();
                opCodeAddr = searchBytes(PCM, dtcSearchConfigs[configIndex].StatusSearch, 0, PCM.fsize);
                if (opCodeAddr == uint.MaxValue)
                {
                    return "DTC Search: Can't find status table";
                }
                statusAddr = BEToUint32(PCM.buf, opCodeAddr + (uint)searchStringAddressOffset(dtcSearchConfigs[configIndex].StatusSearch)) + (uint)dtcSearchConfigs[configIndex].StatusOffset;
                Debug.WriteLine("DTC status table address: " + statusAddr.ToString("X"));
                if (dtcSearchConfigs[configIndex].MilTable == "afterstatus")
                {
                    milAddr = (uint)(statusAddr + dtcCodes.Count + (uint)dtcSearchConfigs[configIndex].MilOffset);
                    if (PCM.xmlFile == "p01-p59" && PCM.buf[milAddr - 1] == 0xFF) milAddr++; //P59 hack: If there is FF before first byte, skip first byte 
                    milAddrList.Add(milAddr);
                }
                else if (dtcSearchConfigs[configIndex].MilTable == "combined")
                {
                    //Do nothing for now
                    milAddr = 0;
                    milAddrList.Add(milAddr);
                    dtcCombined = true;
                }
                else
                {
                    //Search MIL table
                    uint startAddr = 0;
                    for (int i = 0; i < 30; i++)
                    {
                        opCodeAddr = searchBytes(PCM, dtcSearchConfigs[configIndex].MilSearch, startAddr, PCM.fsize);
                        if (opCodeAddr < uint.MaxValue)
                        {

                            milAddr = BEToUint32(PCM.buf, opCodeAddr + (uint)searchStringAddressOffset(dtcSearchConfigs[configIndex].MilSearch)) + (uint)dtcSearchConfigs[configIndex].MilOffset;
                            if (milAddr < PCM.fsize) //Hit
                            {
                                milAddrList.Add(milAddr);
                                //break;
                            }
                            startAddr = opCodeAddr + 8;
                        }
                        else
                        {
                            //Not found
                            break;
                        }
                    }
                }

                if (statusAddr >= PCM.fsize)
                {
                    return "DTC search: Status table address out of address range:" + statusAddr.ToString("X8");
                }

                if (milAddrList.Count > 1)
                {
                    //IF have multiple hits for search, use table which starts after FF, ends before FF
                    for (int m = 0; m < milAddrList.Count; m++)
                    {
                        Debug.WriteLine("MIL Start: " + (milAddrList[m] - 1).ToString("X"));
                        Debug.WriteLine("MIL End: " + (milAddrList[m] + dtcCodes.Count).ToString("X"));
                        if (PCM.buf[milAddrList[m] - 2] == 0xFF && PCM.buf[milAddrList[m] + dtcCodes.Count] == 0xFF)
                        {
                            milAddr = milAddrList[m];
                            break;
                        }
                    }
                }
                Debug.WriteLine("MIL: " + milAddr.ToString("X"));
                if (milAddr >= PCM.fsize)
                {
                    return "DTC search: MIL table address out of address range:" + milAddr.ToString("X8");
                }


                //Read DTC status bytes:
                int dtcNr = 0;
                uint addr3 = milAddr;
                for (uint addr2 = statusAddr; dtcNr < dtcCodes.Count; addr2+= (uint)dtcSearchConfigs[configIndex].StatusSteps, addr3+= (uint)dtcSearchConfigs[configIndex].MilSteps)
                {
                    if (PCM.buf[addr2] > 7)
                        break;
                    if (!dtcCombined && PCM.buf[addr2] > 3) //DTC = 0-3
                    {
                        break;
                    }
                    dtcCode dtc = dtcCodes[dtcNr];
                    dtc.statusAddrInt = addr2;
                    dtc.StatusAddr = addr2.ToString("X8");
                    byte statusByte = PCM.buf[addr2];
                    dtc.Status = statusByte;

                    if (dtcCombined)
                    {
                        dtc.StatusTxt = dtcStatusCombined[dtc.Status];
                        if (statusByte > 4)
                            dtc.MilStatus = 1;
                        else
                            dtc.MilStatus = 0;
                    }
                    else
                    {
                        dtc.StatusTxt = dtcStatus[dtc.Status];
                        //Read MIL bytes:
                        dtc.milAddrInt = addr3;
                        dtc.MilAddr = addr3.ToString("X8");
                        dtc.MilStatus = PCM.buf[addr3];
                    }
                    dtcCodes.RemoveAt(dtcNr);
                    dtcCodes.Insert(dtcNr, dtc);
                    dtcNr++;
                }

            }
            catch (Exception ex)
            {
                var st = new StackTrace(ex, true);
                // Get the top stack frame
                var frame = st.GetFrame(st.FrameCount - 1);
                // Get the line number from the stack frame
                var line = frame.GetFileLineNumber();
                return "DTC search, line " + line + ": " + ex.Message;
            }
            return "";
        }
        //Search GM e38/e67 DTC codes
        public string SearchDtcE38(PcmFile PCM)
        {
            try
            {
                if (PCM.OSSegment == -1)
                {
                    return "DTC search: No OS segment??";
                }
                if (PCM.diagSegment == -1)
                {
                    return "DTC search: No Diagnostic segment??";
                }

                //Get codes from OS segment:
                string searchStr = "00 00 00 10 00 11";
                uint extraCode = 0;
                uint extraStatus = 0;

                for (int b = 0; b < PCM.binfile[PCM.OSSegment].SegmentBlocks.Count; b++)
                {
                    uint startAddr = searchBytes(PCM, searchStr, PCM.binfile[PCM.OSSegment].SegmentBlocks[b].Start, PCM.binfile[PCM.OSSegment].SegmentBlocks[b].End);
                    if (startAddr < uint.MaxValue)
                    {
                        startAddr += 2;
                        searchStr = "94 21 ff";
                        uint endAddr = searchBytes(PCM, searchStr, startAddr + 6, PCM.binfile[PCM.OSSegment].SegmentBlocks[b].End);
                        if (endAddr == uint.MaxValue)
                        {
                            return "DTC Search: SearchString: " + searchStr + " not found";
                        }
                        endAddr -= 1;
                        bool dCodes = false;
                        for (uint addr = startAddr; addr < endAddr; addr += 2)
                        {
                            dtcCode dtc = new dtcCode();
                            dtc.codeAddrInt = addr;
                            dtc.CodeAddr = addr.ToString("X8");
                            dtc.codeInt = BEToUint16(PCM.buf, addr);

                            string codeTmp = dtc.codeInt.ToString("X4");
                            if (dCodes && !codeTmp.StartsWith("D"))
                            {
                                extraCode = endAddr - addr;
                                break;
                            }
                            if (codeTmp.StartsWith("D"))
                            {
                                dCodes = true;
                            }
                            dtc.Code = decodeDTC(codeTmp);
                            for (int o = 0; o < OBD2Codes.Count; o++)
                            {
                                if (dtc.Code == OBD2Codes[o].Code)
                                {
                                    dtc.Description = OBD2Codes[o].Description;
                                    break;
                                }
                            }
                            dtcCodes.Add(dtc);
                        }
                        break;
                    }
                }

                int dtcCount = dtcCodes.Count;
                uint tableStart = 0;
                for (int b = 0; b < PCM.binfile[PCM.diagSegment].SegmentBlocks.Count; b++)
                {
                    //Search table which is exactly correct size & includes values 0-7
                    for (uint addr = PCM.binfile[PCM.diagSegment].SegmentBlocks[b].Start; addr < PCM.binfile[PCM.diagSegment].SegmentBlocks[b].End; addr++)
                    {
                        bool valuesOK = true;
                        if (PCM.buf[addr] == 0xFF && PCM.buf[addr + 1] == 0 && PCM.buf[addr + 2] <= 7 && PCM.buf[addr + dtcCount + 3] == 0xFF) //DTC code status is 0-7, FF after table 
                        {
                            valuesOK = true;
                            for (uint a = addr + 2; a < (addr + dtcCount + 2); a++)
                            {
                                if (PCM.buf[a] > 7)
                                {
                                    //This is not DTC table, it can only have values 0-7
                                    valuesOK = false;
                                    break;
                                }
                            }
                            if (valuesOK)
                            {
                                //We found the table!
                                tableStart = addr + 2;
                                Debug.WriteLine("Found DTC code status table (exact size) at address:" + tableStart.ToString("X8"));
                                break;
                            }
                        }
                    }
                }
                if (tableStart == 0) //Not found yet
                {
                    for (int b = 0; b < PCM.binfile[PCM.diagSegment].SegmentBlocks.Count; b++)
                    {
                        //Search table which is bigger than DTC code list & includes values 0-7
                        for (uint addr = PCM.binfile[PCM.diagSegment].SegmentBlocks[b].Start; addr < PCM.binfile[PCM.diagSegment].SegmentBlocks[b].End; addr++)
                        {
                            bool valuesOK = true;
                            if (PCM.buf[addr] == 0xFF && PCM.buf[addr + 1] == 0 && PCM.buf[addr + 2] <= 7) //DTC code status is 0-7, FF after table 
                            {
                                valuesOK = true;
                                uint a;
                                for (a = addr + 2; a < (addr + dtcCount + 2); a++)
                                {
                                    if (PCM.buf[a] > 7)
                                    {
                                        //This is not DTC table, it can only have values 0-7
                                        valuesOK = false;
                                        break;
                                    }
                                }
                                if (valuesOK)
                                {
                                    //We found the table!
                                    for (uint a2 = a; b < PCM.binfile[PCM.diagSegment].SegmentBlocks[b].End; a2++)
                                    {
                                        if (PCM.buf[a2] == 0xFF)
                                        {
                                            extraStatus = a2 - a;
                                            break;
                                        }
                                    }
                                    tableStart = addr + 2;
                                    Debug.WriteLine("Found DTC status table (NOT exact size) at address:" + tableStart.ToString("X8"));
                                    break;
                                }
                            }
                        }

                    }

                }
                Debug.WriteLine("DTC Code table has " + extraCode.ToString() + " extra bytes");
                Debug.WriteLine("DTC status table has " + extraStatus.ToString() + " extra bytes");
                if (tableStart > 0)
                {
                    int dtcNr = 0;
                    for (uint addr2 = tableStart; addr2 < tableStart + dtcCount; addr2++)
                    {
                        if (PCM.buf[addr2] == 0xFF)
                        {
                            return "";
                        }
                        dtcCode dtc = dtcCodes[dtcNr];
                        dtc.statusAddrInt = addr2;
                        dtc.StatusAddr = addr2.ToString("X8");
                        //dtc.Status = PCM.buf[addr2];
                        byte statusByte = PCM.buf[addr2];
                        if (statusByte > 4)
                            dtc.MilStatus = 1;
                        else
                            dtc.MilStatus = 0;
                        dtc.Status = statusByte;
                        dtc.StatusTxt = dtcStatusCombined[dtc.Status];

                        dtcCodes.RemoveAt(dtcNr);
                        dtcCodes.Insert(dtcNr, dtc);
                        dtcNr++;
                    }
                }
            }
            catch (Exception ex)
            {
                var st = new StackTrace(ex, true);
                // Get the top stack frame
                var frame = st.GetFrame(st.FrameCount - 1);
                // Get the line number from the stack frame
                var line = frame.GetFileLineNumber();
                return "DTC search, line " + line + ": " + ex.Message;
            }
            return "";
        }

    }

}
