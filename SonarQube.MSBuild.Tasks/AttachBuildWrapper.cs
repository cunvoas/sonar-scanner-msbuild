﻿//-----------------------------------------------------------------------
// <copyright file="AttachBuildWrapper.cs" company="SonarSource SA and Microsoft Corporation">
//   Copyright (c) SonarSource SA and Microsoft Corporation.  All rights reserved.
//   Licensed under the MIT License. See License.txt in the project root for license information.
// </copyright>
//-----------------------------------------------------------------------

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using SonarQube.Common;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace SonarQube.MSBuild.Tasks
{
    /// <summary>
    /// MSBuild task to notify the BuildWrapper that the build of C++ project has started
    /// </summary>
    public class AttachBuildWrapper : Task
    {
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        // See https://jira.sonarsource.com/browse/CPP-1458 for the specification of the files contained in the Cpp static resource zip
        private const string BuildWrapperExeName32 = "build-wrapper-win-x86-32.exe";
        private const string BuildWrapperExeName64 = "build-wrapper-win-x86-64.exe";
        private const string AttachedBinaryFileName32 = "interceptor32.dll";
        private const string AttachedBinaryFileName64 = "interceptor64.dll";

        private static readonly string[] RequiredFileNames = new string[] { BuildWrapperExeName32, BuildWrapperExeName64, AttachedBinaryFileName32, AttachedBinaryFileName64 };

        /// <summary>
        /// The length of time to wait for the build wrapper to be launched succesfully
        /// </summary>
        private readonly int buildWrapperTimeoutInMs;

        private const int DefaultBuildWrapperTimeoutInMs = 5000;

        #region Input properties

        /// <summary>
        /// Path to the .sonarqube binary directory containing the build wrapper
        /// </summary>
        [Required]
        public string BinDirectoryPath { get; set; }

        /// <summary>
        /// Directory in which the build wrapper to write the collected data
        /// </summary>
        [Required]
        public string OutputDirectoryPath { get; set; }

        #endregion Input properties

        // Constructor used for testing
        public AttachBuildWrapper() : this(DefaultBuildWrapperTimeoutInMs)
        {
        }

        public AttachBuildWrapper(int buildWrapperTimeoutInMs)
            : base()
        {
            this.buildWrapperTimeoutInMs = buildWrapperTimeoutInMs;
        }

        public override bool Execute()
        {
            Debug.Assert(!string.IsNullOrWhiteSpace(this.BinDirectoryPath), "Expecting the BinDirectoryPath to have been set");
            Debug.Assert(!string.IsNullOrWhiteSpace(this.OutputDirectoryPath), "Expecting the OutputDirectoryPath to have been set");

            bool success = CheckRequiredFilesExist();
            if (!success)
            {
                return false;
            }

            try
            {
                if (this.IsAlreadyAttached())
                {
                    this.Log.LogMessage(MessageImportance.Low, Resources.BuildWrapper_AlreadyAttached, GetProcessId());
                    success = true;
                }
                else
                {
                    Directory.CreateDirectory(this.OutputDirectoryPath);
                    success = this.Attach();
                }
            }
            catch (Exception ex)
            {
                this.Log.LogErrorFromException(ex, true, true, null);
                success = false;
            }

            return success;
        }

        #region Private methods

        private bool CheckRequiredFilesExist()
        {
            bool allExist = true;

            foreach (string fileName in RequiredFileNames)
            {
                string fullName = Path.Combine(this.BinDirectoryPath, fileName);
                if (!File.Exists(fullName))
                {
                    this.Log.LogError(Resources.BuildWrapper_RequiredFileMissing, fullName);
                    allExist = false;
                }
            }
            return allExist;
        }

        /// <summary>
        /// Checks whether the build wrapper is already attached
        /// </summary>
        private bool IsAlreadyAttached()
        {
            string markerDllName = (Environment.Is64BitProcess ? AttachedBinaryFileName64 : AttachedBinaryFileName32);

            string fullMarkerDllPath = Path.Combine(this.BinDirectoryPath, markerDllName);

            return IsModuleLoaded(fullMarkerDllPath);
        }

        private static bool IsModuleLoaded(string fullPath)
        {
            IntPtr h = GetModuleHandle(fullPath);
            if (h != IntPtr.Zero)
            {
                return true;
            }
            return false;
        }

        /// <summary>
        /// Launches the build wrapper and asks it to attach to the current process.
        /// Returns whether the build wrapper was succesfully attached or not.
        /// </summary>
        private bool Attach()
        {
            string currentPID = GetProcessId();

            // Choose either the 32- or 64-bit version of the monitor to match the current process
            string exeName = (Environment.Is64BitProcess ? BuildWrapperExeName64 : BuildWrapperExeName32);
            string monitorExeFilePath = Path.Combine(this.BinDirectoryPath, exeName);

            ProcessRunnerArguments args = new ProcessRunnerArguments(monitorExeFilePath, new MSBuildLoggerAdapter(this.Log));
            args.TimeoutInMilliseconds = this.buildWrapperTimeoutInMs;

            // See https://jira.sonarsource.com/browse/CPP-1469 for the specification of the arguments to pass to the Cpp plugin
            // --msbuild - task < PID > < OUTPUT_DIR >
            args.CmdLineArgs = new string[] {
                "--msbuild-task",
                currentPID,
                Path.GetFullPath(OutputDirectoryPath) // make sure the path is in canonical form
            };

            this.Log.LogMessage(MessageImportance.Low, Resources.BuildWrapper_WaitingForAttach, currentPID);

            ProcessRunner runner = new ProcessRunner();
            bool success = runner.Execute(args);

            if (success)
            {
                this.Log.LogMessage(MessageImportance.Low, Resources.BuildWrapper_AttachedSuccessfully);
            }
            else
            {
                this.Log.LogError(Resources.BuildWrapper_FailedToAttach);
            }

            return success;
        }

        private static string GetProcessId()
        {
            return Process.GetCurrentProcess().Id.ToString();
        }

        #endregion Private methods

    }
}