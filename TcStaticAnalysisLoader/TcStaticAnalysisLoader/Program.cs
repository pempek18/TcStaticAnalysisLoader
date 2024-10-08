﻿/* 
The MIT License(MIT)

Copyright(c) 2018 Jakob Sagatowski

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/
using EnvDTE80;
using NDesk.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text;
using System.Threading.Tasks;
using TCatSysManagerLib;

namespace AllTwinCAT.TcStaticAnalysisLoader
{
    class Program {
        private static string VisualStudioSolutionFilePath = null;
        private static string TwinCATProjectFilePath = null;

        [STAThread]
        static int Main(string[] args) {
            bool showHelp = false;

            OptionSet options = new OptionSet()
                .Add("v=|VisualStudioSolutionFilePath=", v => VisualStudioSolutionFilePath = v)
                .Add("t=|TwinCATProjectFilePath=", t => TwinCATProjectFilePath = t)
                .Add("?|h|help", h => showHelp = h != null);

            try {
                options.Parse(args);
            }
            catch (OptionException e) {
                Console.WriteLine(e.Message);
                Console.WriteLine("Try `TcStaticAnalysisLoader --help' for more information.");
                return Constants.RETURN_ERROR;
            }
            options.Parse(args);

            Console.WriteLine("TcStaticAnalysisLoader.exe : argument 1: " + VisualStudioSolutionFilePath);
            Console.WriteLine("TcStaticAnalysisLoader.exe : argument 2: " + TwinCATProjectFilePath);

            /* Make sure the user has supplied the paths for both the Visual Studio solution file
             * and the TwinCAT project file. Also verify that these two files exists.
             */
            if (showHelp || VisualStudioSolutionFilePath == null || TwinCATProjectFilePath == null) {
                DisplayHelp(options);
                return Constants.RETURN_ERROR;
            }
            if (!File.Exists(VisualStudioSolutionFilePath)) {
                Console.WriteLine("ERROR: Visual studio solution " + VisualStudioSolutionFilePath + " does not exist!");
                return Constants.RETURN_ERROR;
            }
            if (!File.Exists(TwinCATProjectFilePath)) {
                Console.WriteLine("ERROR : TwinCAT project file " + TwinCATProjectFilePath + " does not exist!");
                return Constants.RETURN_ERROR;
            }


            /* Find visual studio version */
            string vsVersion = "";
            string line;
            bool foundVsVersionLine = false;
            System.IO.StreamReader file = new System.IO.StreamReader(@VisualStudioSolutionFilePath);
            while ((line = file.ReadLine()) != null) {
                if (line.StartsWith("VisualStudioVersion")) {
                    string version = line.Substring(line.LastIndexOf('=') + 2);
                    Console.WriteLine("In Visual Studio solution file, found visual studio version " +version);
                    string[] numbers = version.Split('.');
                    string major = numbers[0];
                    string minor = numbers[1];

                    bool isNumericMajor = int.TryParse(major, out int n);
                    bool isNumericMinor = int.TryParse(minor, out int n2);

                    if (isNumericMajor && isNumericMinor) {
                        vsVersion = major + "." + minor;
                        foundVsVersionLine = true;
                    }
                    break;
                }
            }
            file.Close();

            if (!foundVsVersionLine) {
                Console.WriteLine("Did not find Visual studio version in Visual studio solution file");
                return Constants.RETURN_ERROR;
            }

            /* Find TwinCAT project version */
            string tcVersion = "";
            bool foundTcVersionLine = false;
            file = new System.IO.StreamReader(@TwinCATProjectFilePath);
            while ((line = file.ReadLine()) != null) {
                if (line.Contains("TcVersion")) {
                    string version = line.Substring(line.LastIndexOf("TcVersion=\""));
                    int pFrom = version.IndexOf("TcVersion=\"") + "TcVersion=\"".Length;
                    int pTo = version.LastIndexOf("\">");
                    if (pTo > pFrom) {
                        tcVersion = version.Substring(pFrom, pTo - pFrom);
                        foundTcVersionLine = true;
                        Console.WriteLine("In TwinCAT project file, found version " + tcVersion);
                    }
                    break;
                }
            }
            file.Close();
            if (!foundTcVersionLine) {
                Console.WriteLine("Did not find TcVersion in TwinCAT project file");
                return Constants.RETURN_ERROR;
            }


            /* Make sure TwinCAT version is at minimum version 3.1.4022.0 as the static code
             * analysis tool is only supported from this version and onward
             */
            var versionMin = new Version(Constants.MIN_TC_VERSION_FOR_SC_ANALYSIS);
            var versionDetected = new Version(tcVersion);
            var compareResult = versionDetected.CompareTo(versionMin);
            if (compareResult < 0) {
                Console.WriteLine("The detected TwinCAT version in the project does not support TE1200 static code analysis");
                Console.WriteLine("The minimum version that supports TE1200 is " + Constants.MIN_TC_VERSION_FOR_SC_ANALYSIS);
                return Constants.RETURN_ERROR;
            }
            
            MessageFilter.Register();

            /* Make sure the DTE loads with the same version of Visual Studio as the
             * TwinCAT project was created in
             */
            string VisualStudioProgId = "VisualStudio.DTE." + vsVersion;
            Type type = Type.GetTypeFromProgID(VisualStudioProgId);
            if (type == null)
            {
                Console.WriteLine($"Unable to find type from ProgID: {VisualStudioProgId}");
                return Constants.RETURN_ERROR;
            }
            DTE2 dte = (DTE2)Activator.CreateInstance(type);
            dte.SuppressUI = true;
            dte.MainWindow.Visible = false;
            EnvDTE.Solution visualStudioSolution = dte.Solution;
            visualStudioSolution.Open(@VisualStudioSolutionFilePath);
            EnvDTE.Project pro = visualStudioSolution.Projects.Item(1);
            ITcRemoteManager remoteManager = dte.GetObject("TcRemoteManager") as ITcRemoteManager;
            remoteManager.Version = tcVersion;
            var settings = dte.GetObject("TcAutomationSettings");
            //settings.SilentMode = true; // Only available from TC3.1.4020.0 and above

            /* Build the solution and collect any eventual errors. Make sure to
             * filter out everything that is 
             * - Either a warning or an error
             * - Starts with the string "SA", which is everything from the TE1200
             *   static code analysis tool
             */
            visualStudioSolution.SolutionBuild.Clean(true);
            visualStudioSolution.SolutionBuild.Build(true);

            ErrorItems errors = dte.ToolWindows.ErrorList.ErrorItems;

            Console.WriteLine("Errors count: " + errors.Count);
            int tcStaticAnalysisWarnings = 0;
            int tcStaticAnalysisErrors = 0;
            for (int i = 1; i <= errors.Count; i++) {
                ErrorItem item = errors.Item(i);
                if (item.Description.StartsWith("SA") && (item.ErrorLevel != vsBuildErrorLevel.vsBuildErrorLevelLow)) {
                    Console.WriteLine("Description: " + item.Description);
                    Console.WriteLine("ErrorLevel: " + item.ErrorLevel);
                    Console.WriteLine("Filename: " + item.FileName);
                    if (item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelMedium)
                        tcStaticAnalysisWarnings++;
                    else if (item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelHigh)
                        tcStaticAnalysisErrors++;
                }
            }

            dte.Quit();

            MessageFilter.Revoke();

            /* Return the result to the user */
            if (tcStaticAnalysisErrors > 0)
                return Constants.RETURN_ERROR;
            else if (tcStaticAnalysisWarnings > 0)
                return Constants.RETURN_UNSTABLE;
            else
                return Constants.RETURN_SUCCESSFULL;
        }

        static void DisplayHelp(OptionSet p) {
            Console.WriteLine("Usage: TcStaticAnalysisLoader [OPTIONS]");
            Console.WriteLine("Loads the TwinCAT static code analysis loader program with the selected visual studio solution and TwinCAT project.");
            Console.WriteLine("Example: TcStaticAnalysisLoader -v \"C:\\Jenkins\\bitBucket\\TcProject\\TcProject.sln\" -t \"C:\\Jenkins\\bitBucket\\TcProject\\PlcProject1\\PlcProj.tsproj\"");
            Console.WriteLine();
            Console.WriteLine("Options:");
            p.WriteOptionDescriptions(Console.Out);
        }
    }
}
