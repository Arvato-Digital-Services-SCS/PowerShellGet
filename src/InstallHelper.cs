// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using static System.Environment;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.PowerShell.PowerShellGet.RepositorySettings;
using MoreLinq.Extensions;
using Newtonsoft.Json;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace Microsoft.PowerShell.PowerShellGet.Cmdlets
{
    /// <summary>
    /// Find helper class
    /// </summary>
    class InstallHelper : PSCmdlet
    {
        private CancellationToken cancellationToken;
        private readonly bool update;
        private readonly PSCmdlet cmdletPassedIn;

        // This will be a list of all the repository caches
        public static readonly List<string> RepoCacheFileName = new List<string>();
        public static readonly string RepositoryCacheDir = Path.Combine(Environment.GetFolderPath(SpecialFolder.LocalApplicationData), "PowerShellGet", "RepositoryCache");
        public static readonly string osPlatform = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
        private string programFilesPath;
        private string myDocumentsPath;
        private string psPath;
        private string psModulesPath;
        private string psScriptsPath;
        private string psInstalledScriptsInfoPath;
        private List<string> psModulesPathAllDirs;
        private List<string> psScriptsPathAllDirs;
        private List<string> pkgsLeftToInstall;


        public InstallHelper(bool update, CancellationToken cancellationToken, PSCmdlet cmdletPassedIn)
        {
            this.update = update;
            this.cancellationToken = cancellationToken;
            this.cmdletPassedIn = cmdletPassedIn;
        }

        public void ProcessInstallParams(string[] _name, string _version, bool _prerelease, string[] _repository, string _scope, bool _acceptLicense, bool _quiet, bool _reinstall, bool _force, bool _trustRepository, bool _noClobber, PSCredential _credential, string _requiredResourceFile, string _requiredResourceJson, Hashtable _requiredResourceHash)
        {
            var consoleIsElevated = false;
            var isWindowsPS = false;
#if NET472
            var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            consoleIsElevated = (id.Owner != id.User);
            isWindowsPS = true;

            myDocumentsPath = Path.Combine(Environment.GetFolderPath(SpecialFolder.MyDocuments), "WindowsPowerShell");
            programFilesPath = Path.Combine(Environment.GetFolderPath(SpecialFolder.ProgramFiles), "WindowsPowerShell");
#else
            // If Windows OS
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                myDocumentsPath = Path.Combine(Environment.GetFolderPath(SpecialFolder.MyDocuments), "PowerShell");
                programFilesPath = Path.Combine(Environment.GetFolderPath(SpecialFolder.ProgramFiles), "PowerShell");
            }
            else
            {
                // Paths are the same for both Linux and MacOS
                myDocumentsPath = Path.Combine(Environment.GetFolderPath(SpecialFolder.LocalApplicationData), "Powershell");
                programFilesPath = Path.Combine("usr", "local", "share", "Powershell");
            }
#endif
            cmdletPassedIn.WriteVerbose(string.Format("Current user scope installation path: {0}", myDocumentsPath));
            cmdletPassedIn.WriteVerbose(string.Format("All users scope installation path: {0}", programFilesPath));


            // If Scope is AllUsers and there is no console elevation
            if (!string.IsNullOrEmpty(_scope) && _scope.Equals("AllUsers") && !consoleIsElevated)
            {
                // Throw an error when Install-PSResource is used as a non-admin user and '-Scope AllUsers'
                throw new System.ArgumentException(string.Format(CultureInfo.InvariantCulture, "Install-PSResource requires admin privilege for AllUsers scope."));
            }

            // If no scope is specified (whether or not the console is elevated) default installation will be to CurrentUser
            // If running under admin on Windows with PowerShell less than PS6, default will be AllUsers
            if (string.IsNullOrEmpty(_scope))
            {
                // If non-Windows or non-elevated default scope will be current user
                _scope = "CurrentUser";

                // If Windows and elevated default scope will be all users 
                if (isWindowsPS && consoleIsElevated)
                {
                    _scope = "AllUsers";
                }
            }
            cmdletPassedIn.WriteVerbose(string.Format("Scope is: {0}", _scope));

            psPath = string.Equals(_scope, "AllUsers") ? programFilesPath : myDocumentsPath;
            psModulesPath = Path.Combine(psPath, "Modules");
            psScriptsPath = Path.Combine(psPath, "Scripts");
            psInstalledScriptsInfoPath = Path.Combine(psScriptsPath, "InstalledScriptInfos");
            psModulesPathAllDirs = (Directory.GetDirectories(psModulesPath)).ToList();
            // Get the script metadata XML files from the 'InstalledScriptInfos' directory
            psScriptsPathAllDirs = (Directory.GetFiles(psInstalledScriptsInfoPath)).ToList();

            Dictionary<string, PkgParams> pkgsinJson = new Dictionary<string, PkgParams>();
            Dictionary<string, string> jsonPkgsNameVersion = new Dictionary<string, string>();

            if (_requiredResourceFile != null)
            {
                var resolvedReqResourceFile = SessionState.Path.GetResolvedPSPathFromPSPath(_requiredResourceFile).FirstOrDefault().Path;
                cmdletPassedIn.WriteDebug("Resolved required resource file path is: " + resolvedReqResourceFile);

                if (!File.Exists(resolvedReqResourceFile))
                {
                    var exMessage = String.Format("The RequiredResourceFile does not exist.  Please try specifying a path to a valid .json or .psd1 file");
                    var ex = new ArgumentException(exMessage);
                    var RequiredResourceFileDoesNotExist = new ErrorRecord(ex, "RequiredResourceFileDoesNotExist", ErrorCategory.ObjectNotFound, null);

                    cmdletPassedIn.ThrowTerminatingError(RequiredResourceFileDoesNotExist);
                }

                if (resolvedReqResourceFile.EndsWith(".psd1"))
                {
                    // TODO:  implement after implementing publish
                    throw new Exception("This feature is not yet implemented");
                    return;
                }
                else if (resolvedReqResourceFile.EndsWith(".json"))
                {
                    // If json file
                    using (StreamReader sr = new StreamReader(resolvedReqResourceFile))
                    {
                        _requiredResourceJson = sr.ReadToEnd();
                    }

                    try
                    {
                        pkgsinJson = JsonConvert.DeserializeObject<Dictionary<string, PkgParams>>(_requiredResourceJson, new JsonSerializerSettings { MaxDepth = 6 });
                    }
                    catch (Exception e)
                    {
                        var exMessage = String.Format("Argument for parameter -RequiredResource is not in proper json format.  Make sure argument is either a hashtable or a json object.");
                        var ex = new ArgumentException(exMessage);
                        var RequiredResourceNotInProperJsonFormat = new ErrorRecord(ex, "RequiredResourceNotInProperJsonFormat", ErrorCategory.ObjectNotFound, null);

                        cmdletPassedIn.ThrowTerminatingError(RequiredResourceNotInProperJsonFormat);
                    }
                }
                else
                {
                   // throw new Exception("The RequiredResourceFile does not have the proper file extension.  Please try specifying a path to a valid .json or .psd1 file");
                    var exMessage = String.Format("The RequiredResourceFile does not have the proper file extension.  Please try specifying a path to a valid .json or .psd1 file");
                    var ex = new ArgumentException(exMessage);
                    var RequiredResourceFileExtensionError = new ErrorRecord(ex, "RequiredResourceFileExtensionError", ErrorCategory.ObjectNotFound, null);

                    cmdletPassedIn.ThrowTerminatingError(RequiredResourceFileExtensionError);
                }
            }

            if (_requiredResourceHash != null)
            {
                string jsonString = "";
                try
                {
                    jsonString = _requiredResourceHash.ToJson();
                }
                catch (Exception e)
                {
                    var exMessage = String.Format("Argument for parameter -RequiredResource is not in proper json format.  Make sure argument is either a hashtable or a json object.");
                    var ex = new ArgumentException(exMessage);
                    var RequiredResourceNotInProperJsonFormat = new ErrorRecord(ex, "RequiredResourceNotInProperJsonFormat", ErrorCategory.ObjectNotFound, null);

                    cmdletPassedIn.ThrowTerminatingError(RequiredResourceNotInProperJsonFormat);
                }

                PkgParams pkg = null;
                try
                {
                    pkg = JsonConvert.DeserializeObject<PkgParams>(jsonString, new JsonSerializerSettings { MaxDepth = 6 });
                }
                catch (Exception e)
                {
                    var exMessage = String.Format("Argument for parameter -RequiredResource is not in proper json format.  Make sure argument is either a hashtable or a json object.");
                    var ex = new ArgumentException(exMessage);
                    var RequiredResourceNotInProperJsonFormat = new ErrorRecord(ex, "RequiredResourceNotInProperJsonFormat", ErrorCategory.ObjectNotFound, null);

                    cmdletPassedIn.ThrowTerminatingError(RequiredResourceNotInProperJsonFormat);
                }

                ProcessRepositories(new string[] { pkg.Name }, pkg.Version, pkg.Prerelease, new string[] { pkg.Repository }, pkg.Scope, pkg.AcceptLicense, pkg.Quiet, pkg.Reinstall, pkg.Force, pkg.TrustRepository, pkg.NoClobber, pkg.Credential);

                return;
            }

            if (_requiredResourceJson != null)
            {
                if (!pkgsinJson.Any())
                {
                    try
                    {
                        pkgsinJson = JsonConvert.DeserializeObject<Dictionary<string, PkgParams>>(_requiredResourceJson, new JsonSerializerSettings { MaxDepth = 6 });
                    }
                    catch (Exception e)
                    {
                        try
                        {
                            jsonPkgsNameVersion = JsonConvert.DeserializeObject<Dictionary<string, string>>(_requiredResourceJson, new JsonSerializerSettings { MaxDepth = 6 });
                        }
                        catch (Exception e2)
                        {
                            var exMessage = String.Format("Argument for parameter -RequiredResource is not in proper json format.  Make sure argument is either a hashtable or a json object.");
                            var ex = new ArgumentException(exMessage);
                            var RequiredResourceNotInProperJsonFormat = new ErrorRecord(ex, "RequiredResourceNotInProperJsonFormat", ErrorCategory.ObjectNotFound, null);

                            cmdletPassedIn.ThrowTerminatingError(RequiredResourceNotInProperJsonFormat);
                        }
                    }
                }

                foreach (var pkg in jsonPkgsNameVersion)
                {
                    ProcessRepositories(new string[] { pkg.Key }, pkg.Value, false, null, null, false, false, false, false, false, false, null);
                }

                foreach (var pkg in pkgsinJson)
                {
                    ProcessRepositories(new string[] { pkg.Key }, pkg.Value.Version, pkg.Value.Prerelease, new string[] { pkg.Value.Repository }, pkg.Value.Scope, pkg.Value.AcceptLicense, pkg.Value.Quiet, pkg.Value.Reinstall, pkg.Value.Force, pkg.Value.TrustRepository, pkg.Value.NoClobber, pkg.Value.Credential);
                }
                return;
            }

            ProcessRepositories(_name, _version, _prerelease, _repository, _scope, _acceptLicense, _quiet, _reinstall, _force, _trustRepository, _noClobber, _credential);
        }

        public void ProcessRepositories(string[] packageNames, string version, bool prerelease, string[] repository, string scope, bool acceptLicense, bool quiet, bool reinstall, bool force, bool trustRepository, bool noClobber, PSCredential credential)
        {
            var r = new RespositorySettings();
            var listOfRepositories = r.Read(repository);

            pkgsLeftToInstall = packageNames.ToList();

            var yesToAll = false;
            var noToAll = false;
            var repositoryIsNotTrusted = "Untrusted repository";
            var queryInstallUntrustedPackage = "You are installing the modules from an untrusted repository. If you trust this repository, change its Trusted value by running the Set-PSResourceRepository cmdlet. Are you sure you want to install the PSresource from '{0}' ?";

            foreach (var repoName in listOfRepositories)
            {
                var sourceTrusted = false;

                if (string.Equals(repoName.Properties["Trusted"].Value.ToString(), "false", StringComparison.InvariantCultureIgnoreCase) && !trustRepository && !force)
                {
                    cmdletPassedIn.WriteDebug("Checking if untrusted repository should be used");

                    if (!(yesToAll || noToAll))
                    {
                        // Prompt for installation of package from untrusted repository
                        var message = string.Format(CultureInfo.InvariantCulture, queryInstallUntrustedPackage, repoName.Properties["Name"].Value.ToString());
                        sourceTrusted = cmdletPassedIn.ShouldContinue(message, repositoryIsNotTrusted, true, ref yesToAll, ref noToAll);
                    }
                }
                else
                {
                    sourceTrusted = true;
                }

                if (sourceTrusted || yesToAll)
                {
                    cmdletPassedIn.WriteDebug("Untrusted repository accepted as trusted source.");
                    // Try to install-- returns any pkgs that weren't found
                    // If it can't find the pkg in one repository, it'll look for it in the next repo in the list
                    var returnedPkgsNotInstalled = InstallPkgs(repoName.Properties["Url"].Value.ToString(), pkgsLeftToInstall, packageNames, version, prerelease, scope, acceptLicense, quiet, reinstall, force, trustRepository, noClobber, credential);
                    if (!pkgsLeftToInstall.Any())
                    {
                        return;
                    }
                    pkgsLeftToInstall = returnedPkgsNotInstalled;
                }
            }
        }

        // Installing a package will have a transactional behavior:
        // Package and its dependencies will be saved into a tmp folder
        // and will only be properly installed if all dependencies are found successfully.
        // Once package is installed, we want to resolve and install all dependencies.
        public List<string> InstallPkgs(string repositoryUrl, List<string> pkgsLeftToInstall, string[] packageNames, string version, bool prerelease, string scope, bool acceptLicense, bool quiet, bool reinstall, bool force, bool trustRepository, bool noClobber, PSCredential credential)
        {
            PackageSource source = new PackageSource(repositoryUrl);

            if (credential != null)
            {
                string password = new NetworkCredential(string.Empty, credential.Password).Password;
                source.Credentials = PackageSourceCredential.FromUserInput(repositoryUrl, credential.UserName, password, true, null);
            }

            var provider = FactoryExtensionsV3.GetCoreV3(NuGet.Protocol.Core.Types.Repository.Provider);
            SourceRepository repository = new SourceRepository(source, provider);
            SearchFilter filter = new SearchFilter(prerelease);
            SourceCacheContext context = new SourceCacheContext();

            // TODO:  proper error handling here
            PackageMetadataResource resourceMetadata = null;
            try
            {
                resourceMetadata = repository.GetResourceAsync<PackageMetadataResource>().GetAwaiter().GetResult();
            }
            catch
            {
                var exMessage = String.Format("Error retreiving repository resource");
                var ex = new ArgumentException(exMessage);
                var ErrorCreatingRepositoryResource = new ErrorRecord(ex, "ErrorCreatingRepositoryResource", ErrorCategory.ObjectNotFound, null);

                cmdletPassedIn.ThrowTerminatingError(ErrorCreatingRepositoryResource);
            }

            foreach (var n in packageNames)
            {
                IPackageSearchMetadata filteredFoundPkgs = null;

                VersionRange versionRange = null;
                if (version == null)  
                {
                    // ensure that the latst version is returned first (the ordering of versions differ
                    // TODO: proper error handling
                    try
                    {
                        filteredFoundPkgs = (resourceMetadata.GetMetadataAsync(n, prerelease, false, context, NullLogger.Instance, cancellationToken).GetAwaiter().GetResult()
                            .OrderByDescending(p => p.Identity.Version, VersionComparer.VersionRelease)
                            .FirstOrDefault());

                            // Check if exact version
                            NuGetVersion nugetVersion;
                            NuGetVersion.TryParse(filteredFoundPkgs.Identity.Version.ToString(), out nugetVersion);

                            if (nugetVersion != null)
                            {
                                versionRange = new VersionRange(nugetVersion, true, nugetVersion, true, null, null);
                            }
                    }
                    catch
                    {
                        var exMessage = String.Format("Could not find package {0}", n);
                        var ex = new ArgumentException(exMessage);
                        var ErrorCreatingRepositoryResource = new ErrorRecord(ex, "ErrorCreatingRepositoryResource", ErrorCategory.ObjectNotFound, null);

                        cmdletPassedIn.ThrowTerminatingError(ErrorCreatingRepositoryResource);
                    }
                }
                else
                {
                    // Check if exact version
                    NuGetVersion nugetVersion;
                    NuGetVersion.TryParse(version, out nugetVersion);

                    if (nugetVersion != null)
                    {
                        // Exact version
                        versionRange = new VersionRange(nugetVersion, true, nugetVersion, true, null, null);
                    }
                    else
                    {
                        // Check if version range
                        versionRange = VersionRange.Parse(version);
                    }
                    cmdletPassedIn.WriteVerbose(string.Format("Version is: {0}", versionRange.ToString()));

                    // Search for packages within a version range
                    // ensure that the latst version is returned first (the ordering of versions differ
                    filteredFoundPkgs = (resourceMetadata.GetMetadataAsync(n, prerelease, false, context, NullLogger.Instance, cancellationToken).GetAwaiter().GetResult()
                        .Where(p => versionRange.Satisfies(p.Identity.Version))
                        .OrderByDescending(p => p.Identity.Version, VersionComparer.VersionRelease)
                        .FirstOrDefault());
                }

                List<IPackageSearchMetadata> foundDependencies = new List<IPackageSearchMetadata>();

                // Found package to install, now search for dependencies
                if (filteredFoundPkgs != null)
                {
                    // TODO: improve dependency search
                    // This function recursively finds all dependencies
                    foundDependencies.AddRange(FindDependenciesFromSource(filteredFoundPkgs, resourceMetadata, context, prerelease, reinstall));
                }

                // Check which pkgs actually need to be installed
                List<IPackageSearchMetadata> pkgsToInstall = new List<IPackageSearchMetadata>();                
                pkgsToInstall.Add(filteredFoundPkgs);
                pkgsToInstall.AddRange(foundDependencies);

                // We have a list of everything that needs to be installed
                // Check the system to see if that particular package AND package version is there
                // If it is, remove it from the list of pkgs to install
                if (versionRange != null)
                {
                    foreach (var name in packageNames)
                    {
                        var pkgDirName = Path.Combine(psModulesPath, name);
                        var pkgDirNameScript = Path.Combine(psInstalledScriptsInfoPath, name + "_InstalledScriptInfo.xml");

                        // Check to see if the package dir exists in the Modules path or if the script metadata file exists in the Scripts path
                        if (psModulesPathAllDirs.Contains(pkgDirName, StringComparer.OrdinalIgnoreCase)
                            || psScriptsPathAllDirs.Contains(pkgDirNameScript, StringComparer.OrdinalIgnoreCase))
                        {
                            // Then check to see if the package version exists in the Modules path
                            var pkgDirVersion = (Directory.GetDirectories(pkgDirName)).ToList();
                        
                            List<string> pkgVersion = new List<string>();
                            foreach (var path in pkgDirVersion)
                            {
                                pkgVersion.Add(Path.GetFileName(path));
                            }

                            // Remove any pkg versions that are not formatted correctly, eg:  2.2.4.1x
                            String[] pkgVersions = pkgVersion.ToArray();

                            foreach (var installedPkgVer in pkgVersions)
                            {
                                if (!NuGetVersion.TryParse(installedPkgVer, out NuGetVersion pkgVer))
                                {
                                    pkgVersion.Remove(installedPkgVer);
                                }
                            }

                            // These are all the packages already installed
                            var pkgsAlreadyInstalled = pkgVersion.FindAll(p => versionRange.Satisfies(NuGetVersion.Parse(p)));

                            if (pkgsAlreadyInstalled.Any() && !reinstall)
                            {
                                // Remove the pkg from the list of pkgs that need to be installed
                                var pkgsToRemove = pkgsToInstall.Find(p => string.Equals(p.Identity.Id, name, StringComparison.CurrentCultureIgnoreCase));

                                pkgsToInstall.Remove(pkgsToRemove);
                                pkgsLeftToInstall.Remove(name);
                            }
                        }
                        else if (update)
                        {
                            // Not installed throw terminating error
                            var exMessage = String.Format("Module '{0}' was not updated because no valid module was found in the module directory.Verify that the module is located in the folder specified by $env: PSModulePath.", name);
                            var ex = new ArgumentException(exMessage);  // System.ArgumentException vs PSArgumentException
                            var moduleNotInstalledError = new ErrorRecord(ex, "ModuleNotInstalled", ErrorCategory.ObjectNotFound, null);

                            cmdletPassedIn.ThrowTerminatingError(moduleNotInstalledError);
                        }
                    }
                }
                else
                {
                    foreach (var name in packageNames)
                    {
                        // case sensitivity issues here!
                        var dirName = Path.Combine(psModulesPath, name);
                        // Example script metadata file name format: 'install-kubectl_InstalledScriptInfo.xml'
                        var dirNameScript = Path.Combine(psInstalledScriptsInfoPath, name + "_InstalledScriptInfo.xml");  

                        // Check to see if the package dir exists in the path
                        if (psModulesPathAllDirs.Contains(dirName, StringComparer.OrdinalIgnoreCase)
                             || psScriptsPathAllDirs.Contains(dirNameScript, StringComparer.OrdinalIgnoreCase))
                        {
                            // then check to see if the package/script exists in the path
                            if ((Directory.Exists(dirName) || Directory.Exists(dirNameScript)) && !reinstall && !update)
                            {
                                // Remove the pkg from the list of pkgs that need to be installed
                                //case sensitivity here 
                                var pkgsToRemove = pkgsToInstall.Find(p => string.Equals(p.Identity.Id, name, StringComparison.CurrentCultureIgnoreCase));

                                pkgsToInstall.Remove(pkgsToRemove);
                                pkgsLeftToInstall.Remove(name);
                            }
                            // if update, check to see if that particular version is in the path.. 
                            // check script version matching too
                        }
                        else if (update)
                        {
                            // Throw module or script not installed terminating error 
                            var exMessage = String.Format("Module '{0}' was not updated because no valid module was found in the module directory.Verify that the module is located in the folder specified by $env: PSModulePath.", name);
                            var ex = new ArgumentException(exMessage);
                            var moduleNotInstalledError = new ErrorRecord(ex, "ModuleNotInstalled", ErrorCategory.ObjectNotFound, null);

                            cmdletPassedIn.ThrowTerminatingError(moduleNotInstalledError);
                        }
                    }
                }

                // Remove any null pkgs
                pkgsToInstall.Remove(null);

                var tempInstallPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                var dir = Directory.CreateDirectory(tempInstallPath);  // should check it gets created properly
                                                                       // To delete file attributes from the existing ones get the current file attributes first and use AND (&) operator
                                                                       // with a mask (bitwise complement of desired attributes combination).
                dir.Attributes = dir.Attributes & ~FileAttributes.ReadOnly;

                // Install everything to a temp path
                foreach (var p in pkgsToInstall)
                {
                    if (!quiet)
                    {
                        int i = 1;
                        int j = 1;
                        /****************************
                        * START PACKAGE INSTALLATION -- start progress bar 
                        *****************************/
                        // Write-Progress -Activity "Search in Progress" - Status "$i% Complete:" - PercentComplete $i

                        int activityId = 0;
                        string activity = "";
                        string statusDescription = "";

                        if (packageNames.ToList().Contains(p.Identity.Id))
                        {
                            // If the pkg exists in one of the names passed in, then we wont include it as a dependent package
                            activityId = 0;
                            activity = string.Format("Installing {0}...", p);
                            statusDescription = string.Format("{0}% Complete:", i++);

                            j = 1;
                        }
                        else
                        {
                            // Child process
                            // Installing dependent package
                            activityId = 1;
                            activity = string.Format("Installing dependent package {0}...", p);
                            statusDescription = string.Format("{0}% Complete:", j);
                        }

                        var progressRecord = new ProgressRecord(activityId, activity, statusDescription);
                        cmdletPassedIn.WriteProgress(progressRecord);
                    }

                    var pkgIdentity = new PackageIdentity(p.Identity.Id, p.Identity.Version);

                    var resource = new DownloadResourceV2FeedProvider();
                    var cacheContext = new SourceCacheContext();
                    var downloadResource = repository.GetResourceAsync<DownloadResource>().GetAwaiter().GetResult();

                    var result = downloadResource.GetDownloadResourceResultAsync(
                        pkgIdentity,
                        new PackageDownloadContext(cacheContext),
                        tempInstallPath,
                        logger: NullLogger.Instance,
                        CancellationToken.None).GetAwaiter().GetResult();

                    // Need to close the .nupkg
                    result.Dispose();


                    // ACCEPT LICENSE
                    // Prompt if module requires license acceptance
                    // Need to read from .psd1 
                    var newVersion = p.Identity.Version.ToString();
                    if (p.Identity.Version.IsPrerelease)
                    {
                        newVersion = p.Identity.Version.ToString().Substring(0, p.Identity.Version.ToString().IndexOf('-'));
                    }

                    var modulePath = Path.Combine(tempInstallPath, pkgIdentity.Id, newVersion);
                    var moduleManifest = Path.Combine(modulePath, pkgIdentity.Id + ".psd1");
                    var requireLicenseAcceptance = false;

                    if (File.Exists(moduleManifest))
                    {
                        using (StreamReader sr = new StreamReader(moduleManifest))
                        {
                            var text = sr.ReadToEnd();

                            var pattern = "RequireLicenseAcceptance\\s*=\\s*\\$true";
                            var patternToSkip1 = "#\\s*RequireLicenseAcceptance\\s*=\\s*\\$true";
                            var patternToSkip2 = "\\*\\s*RequireLicenseAcceptance\\s*=\\s*\\$true";

                            Regex rgx = new Regex(pattern);

                            if (rgx.IsMatch(pattern) && !rgx.IsMatch(patternToSkip1) && !rgx.IsMatch(patternToSkip2))
                            {
                                requireLicenseAcceptance = true;
                            }
                        }

                        if (requireLicenseAcceptance)
                        {
                            // If module requires license acceptance and -AcceptLicense is not passed in, display prompt
                            if (!acceptLicense)
                            {
                                var PkgTempInstallPath = Path.Combine(tempInstallPath, p.Identity.Id, newVersion);
                                var LicenseFilePath = Path.Combine(PkgTempInstallPath, "License.txt");

                                if (!File.Exists(LicenseFilePath))
                                {
                                    var exMessage = "License.txt not Found. License.txt must be provided when user license acceptance is required.";
                                    var ex = new ArgumentException(exMessage);  // System.ArgumentException vs PSArgumentException
                                    var acceptLicenseError = new ErrorRecord(ex, "LicenseTxtNotFound", ErrorCategory.ObjectNotFound, null);

                                    cmdletPassedIn.ThrowTerminatingError(acceptLicenseError);
                                }

                                // Otherwise read LicenseFile 
                                string licenseText = System.IO.File.ReadAllText(LicenseFilePath);
                                var acceptanceLicenseQuery = $"Do you accept the license terms for module '{p.Identity.Id}'.";
                                var message = licenseText + "`r`n" + acceptanceLicenseQuery;

                                var title = "License Acceptance";
                                var yesToAll = false;
                                var noToAll = false;
                                var shouldContinueResult = ShouldContinue(message, title, true, ref yesToAll, ref noToAll);

                                if (yesToAll)
                                {
                                    acceptLicense = true;
                                }
                            }

                            // Check if user agreed to license terms, if they didn't then throw error, otherwise continue to install
                            if (!acceptLicense)
                            {
                                var message = $"License Acceptance is required for module '{p.Identity.Id}'. Please specify '-AcceptLicense' to perform this operation.";
                                var ex = new ArgumentException(message);  // System.ArgumentException vs PSArgumentException
                                var acceptLicenseError = new ErrorRecord(ex, "ForceAcceptLicense", ErrorCategory.InvalidArgument, null);

                                cmdletPassedIn.ThrowTerminatingError(acceptLicenseError);
                            }
                        }
                    }

                    var dirNameVersion = Path.Combine(tempInstallPath, p.Identity.Id, p.Identity.Version.ToString());
                    var nupkgMetadataToDelete = Path.Combine(dirNameVersion, (p.Identity.ToString() + ".nupkg").ToLower());
                    var nupkgToDelete = Path.Combine(dirNameVersion, (p.Identity.ToString() + ".nupkg").ToLower());
                    var nupkgSHAToDelete = Path.Combine(dirNameVersion, (p.Identity.ToString() + ".nupkg.sha512").ToLower());
                    var nuspecToDelete = Path.Combine(dirNameVersion, (p.Identity.Id + ".nuspec").ToLower());

                    File.Delete(nupkgMetadataToDelete);
                    File.Delete(nupkgSHAToDelete);
                    File.Delete(nuspecToDelete);
                    File.Delete(nupkgToDelete);

                    // if it's not a script, do the following:
                    var scriptPath = Path.Combine(dirNameVersion, (p.Identity.Id.ToString() + ".ps1").ToLower());
                    var isScript = File.Exists(scriptPath) ? true : false;

                    // Create PSGetModuleInfo.xml
                    var fullinstallPath = isScript ? Path.Combine(dirNameVersion, (p.Identity.Id + "_InstalledScriptInfo.xml"))
                        : Path.Combine(dirNameVersion, "PSGetModuleInfo.xml");

                    // Create XMLs
                    using (StreamWriter sw = new StreamWriter(fullinstallPath))
                    {
                        var tags = p.Tags.Split(' ');

                        var module = tags.Contains("PSModule") ? "Module" : null;
                        var script = tags.Contains("PSScript") ? "Script" : null;

                        List<string> includesDscResource = new List<string>();
                        List<string> includesCommand = new List<string>();
                        List<string> includesFunction = new List<string>();
                        List<string> includesRoleCapability = new List<string>();
                        List<string> filteredTags = new List<string>();

                        var psDscResource = "PSDscResource_";
                        var psCommand = "PSCommand_";
                        var psFunction = "PSFunction_";
                        var psRoleCapability = "PSRoleCapability_";

                        foreach (var tag in tags)
                        {
                            if (tag.StartsWith(psDscResource))
                            {
                                includesDscResource.Add(tag.Remove(0, psDscResource.Length));
                            }
                            else if (tag.StartsWith(psCommand))
                            {
                                includesCommand.Add(tag.Remove(0, psCommand.Length));
                            }
                            else if (tag.StartsWith(psFunction))
                            {
                                includesFunction.Add(tag.Remove(0, psFunction.Length));
                            }
                            else if (tag.StartsWith(psRoleCapability))
                            {
                                includesRoleCapability.Add(tag.Remove(0, psRoleCapability.Length));
                            }
                            else if (!tag.StartsWith("PSWorkflow_") && !tag.StartsWith("PSCmdlet_") && !tag.StartsWith("PSIncludes_")
                                && !tag.Equals("PSModule") && !tag.Equals("PSScript"))
                            {
                                filteredTags.Add(tag);
                            }
                        }

                        // If NoClobber is specified, ensure command clobbering does not happen
                        if (noClobber)
                        {
                            // This is a primitive implementation  
                            // 1) get all possible paths
                            // 2) search all modules and compare
                            /// Cannot uninstall a module if another module is dependent on it 

                            using (System.Management.Automation.PowerShell pwsh = System.Management.Automation.PowerShell.Create())
                            {
                                // Get all modules
                                var results = pwsh.AddCommand("Get-Module").AddParameter("ListAvailable").Invoke();

                                // Structure of LINQ call:
                                // Results is a collection of PSModuleInfo objects that contain a property listing module commands, "ExportedCommands".
                                // ExportedCommands is collection of PSModuleInfo objects that need to be iterated through to see if any of them are the command we're trying to install
                                // If anything from the final call gets returned, there is a command clobber with this pkg.

                                List<IEnumerable<PSObject>> pkgsWithCommandClobber = new List<IEnumerable<PSObject>>();
                                foreach (string command in includesCommand)
                                {
                                    pkgsWithCommandClobber.Add(results.Where(pkg => ((ReadOnlyCollection<PSModuleInfo>)pkg.Properties["ExportedCommands"].Value).Where(ec => ec.Name.Equals(command, StringComparison.InvariantCultureIgnoreCase)).Any()));
                                }
                                if (pkgsWithCommandClobber.Any())
                                {
                                    var uniqueCommandNames = (pkgsWithCommandClobber.Select(cmd => cmd.ToString()).Distinct()).ToArray();
                                    string strUniqueCommandNames = string.Join(",", uniqueCommandNames);

                                    throw new System.ArgumentException(string.Format(CultureInfo.InvariantCulture, "Command(s) with name(s) '{0}' is already available on this system. Installing '{1}' may override the existing command. If you still want to install '{1}', remove the -NoClobber parameter.", strUniqueCommandNames, p.Identity.Id));
                                }
                            }
                        }

                        Dictionary<string, List<string>> includes = new Dictionary<string, List<string>> {
                            { "DscResource", includesDscResource },
                            { "Command", includesCommand },
                            { "Function", includesFunction },
                            { "RoleCapability", includesRoleCapability }
                        };

                        Dictionary<string, VersionRange> dependencies = new Dictionary<string, VersionRange>();
                        foreach (var depGroup in p.DependencySets)
                        {
                            PackageDependency depPkg = depGroup.Packages.FirstOrDefault();
                            dependencies.Add(depPkg.Id, depPkg.VersionRange);
                        }

                        var psGetModuleInfoObj = new PSObject();
                        // TODO:  Add release notes
                        psGetModuleInfoObj.Members.Add(new PSNoteProperty("Name", p.Identity.Id));
                        psGetModuleInfoObj.Members.Add(new PSNoteProperty("Version", p.Identity.Version));
                        psGetModuleInfoObj.Members.Add(new PSNoteProperty("Type", module != null ? module : (script != null ? script : null)));
                        psGetModuleInfoObj.Members.Add(new PSNoteProperty("Description", p.Description));
                        psGetModuleInfoObj.Members.Add(new PSNoteProperty("Author", p.Authors));
                        psGetModuleInfoObj.Members.Add(new PSNoteProperty("CompanyName", p.Owners));
                        psGetModuleInfoObj.Members.Add(new PSNoteProperty("PublishedDate", p.Published));
                        psGetModuleInfoObj.Members.Add(new PSNoteProperty("InstalledDate", System.DateTime.Now));
                        psGetModuleInfoObj.Members.Add(new PSNoteProperty("LicenseUri", p.LicenseUrl));
                        psGetModuleInfoObj.Members.Add(new PSNoteProperty("ProjectUri", p.ProjectUrl));
                        psGetModuleInfoObj.Members.Add(new PSNoteProperty("IconUri", p.IconUrl));
                        psGetModuleInfoObj.Members.Add(new PSNoteProperty("Includes", includes.ToList()));  
                        psGetModuleInfoObj.Members.Add(new PSNoteProperty("PowerShellGetFormatVersion", "3"));
                        psGetModuleInfoObj.Members.Add(new PSNoteProperty("Dependencies", dependencies.ToList()));
                        psGetModuleInfoObj.Members.Add(new PSNoteProperty("RepositorySourceLocation", repositoryUrl));
                        psGetModuleInfoObj.Members.Add(new PSNoteProperty("Repository", repositoryUrl));
                        psGetModuleInfoObj.Members.Add(new PSNoteProperty("InstalledLocation", null));

                        psGetModuleInfoObj.TypeNames.Add("Microsoft.PowerShell.Commands.PSRepositoryItemInfo");

                        var serializedObj = PSSerializer.Serialize(psGetModuleInfoObj);

                        sw.Write(serializedObj);
                    }

                    // Copy to proper path
                    var installPath = isScript ? psScriptsPath : psModulesPath;
                    var newPath = isScript ? installPath
                        : Path.Combine(installPath, p.Identity.Id.ToString());
                    // When we move the directory over, we'll change the casing of the module directory name from lower case to proper casing.

                    // If script, just move the files over, if module, move the version directory over
                    var tempModuleVersionDir = isScript ? Path.Combine(tempInstallPath, p.Identity.Id.ToLower(), p.Identity.Version.ToString())
                        : Path.Combine(tempInstallPath, p.Identity.Id.ToLower());

                    if (isScript)
                    {
                        var scriptXML = p.Identity.Id + "_InstalledScriptInfo.xml";
                        if (File.Exists(Path.Combine(psScriptsPath, "InstalledScriptInfos", scriptXML)))
                        {
                            File.Delete(Path.Combine(psScriptsPath, "InstalledScriptInfos", scriptXML));
                        }
                        if (File.Exists(Path.Combine(newPath, p.Identity.Id + ".ps1")))
                        {
                            File.Delete(Path.Combine(newPath, p.Identity.Id + ".ps1"));
                        }
                        File.Move(Path.Combine(tempModuleVersionDir, scriptXML), Path.Combine(psScriptsPath, "InstalledScriptInfos", scriptXML));
                        File.Move(Path.Combine(tempModuleVersionDir, p.Identity.Id.ToLower() + ".ps1"), Path.Combine(newPath, p.Identity.Id + ".ps1"));
                    }
                    else
                    {
                        if (!Directory.Exists(newPath))
                        {
                            Directory.Move(tempModuleVersionDir, newPath);
                        }
                        else
                        {
                            tempModuleVersionDir = Path.Combine(tempModuleVersionDir, p.Identity.Version.ToString());

                            var newVersionPath = Path.Combine(newPath, newVersion);

                            if (Directory.Exists(newVersionPath))
                            {
                                // Delete the directory path before replacing it with the new module
                                Directory.Delete(newVersionPath, true);
                            }
                            Directory.Move(tempModuleVersionDir, Path.Combine(newPath, newVersion));
                        }
                    }
                    
                    Directory.Delete(tempInstallPath, true);

                    pkgsLeftToInstall.Remove(n);
                }
            }

            return pkgsLeftToInstall;
        }


        private List<IPackageSearchMetadata> FindDependenciesFromSource(IPackageSearchMetadata pkg, PackageMetadataResource pkgMetadataResource, SourceCacheContext srcContext, bool prerelease, bool reinstall)
        {
            // Dependency resolver
            // This function is recursively called
            // Call the findpackages from source helper (potentially generalize this so it's finding packages from source or cache)
            List<IPackageSearchMetadata> foundDependencies = new List<IPackageSearchMetadata>();

            // 1) Check the dependencies of this pkg
            // 2) For each dependency group, search for the appropriate name and version
            // A dependency group includes all the dependencies for a particular framework
            foreach (var dependencyGroup in pkg.DependencySets)
            {
                foreach (var pkgDependency in dependencyGroup.Packages)
                {
                    // a) Check that the appropriate pkg dependencies exist
                    // Returns all versions from a single package id.
                    var dependencies = pkgMetadataResource.GetMetadataAsync(pkgDependency.Id, prerelease, true, srcContext, NullLogger.Instance, cancellationToken).GetAwaiter().GetResult();

                    // b) Check if the appropriate verion range exists  (if version exists, then add it to the list to return)
                    VersionRange versionRange = null;
                    try
                    {
                        versionRange = VersionRange.Parse(pkgDependency.VersionRange.OriginalString);
                    }
                    catch
                    {
                        Console.WriteLine("Error parsing version range");

                        var exMessage = String.Format("Error parsing version range");
                        var ex = new ArgumentException(exMessage);
                        var ErrorCreatingRepositoryResource = new ErrorRecord(ex, "ErrorCreatingRepositoryResource", ErrorCategory.ObjectNotFound, null);

                        cmdletPassedIn.ThrowTerminatingError(ErrorCreatingRepositoryResource);
                    }

                    // If no version/version range is specified the we just return the latest version
                    IPackageSearchMetadata depPkgToReturn = (versionRange == null ?
                        dependencies.FirstOrDefault() :
                        dependencies.Where(v => versionRange.Satisfies(v.Identity.Version)).FirstOrDefault());

                    // If the pkg already exists on the system, don't add it to the list of pkgs that need to be installed 
                    var dirName = Path.Combine(psModulesPath, pkgDependency.Id);
                    var dependencyAlreadyInstalled = false;

                    // Check to see if the package dir exists in the path
                    if (psModulesPathAllDirs.Contains(dirName, StringComparer.OrdinalIgnoreCase))
                    {
                        // Then check to see if the package exists in the path
                        if (Directory.Exists(dirName))
                        {
                            var pkgDirVersion = (Directory.GetDirectories(dirName)).ToList();
                            List<string> pkgVersion = new List<string>();
                            foreach (var path in pkgDirVersion)
                            {
                                pkgVersion.Add(Path.GetFileName(path));
                            }

                            // These are all the packages already installed
                            NuGetVersion ver;
                            var pkgsAlreadyInstalled = pkgVersion.FindAll(p => NuGetVersion.TryParse(p, out ver) && versionRange.Satisfies(ver));

                            if (pkgsAlreadyInstalled.Any() && !reinstall)
                            {
                                // Don't add the pkg to the list of pkgs that need to be installed
                                dependencyAlreadyInstalled = true;
                            }
                        }
                    }

                    if (!dependencyAlreadyInstalled)
                    {
                        foundDependencies.Add(depPkgToReturn);
                    }

                    // Search for any dependencies the pkg has
                    foundDependencies.AddRange(FindDependenciesFromSource(depPkgToReturn, pkgMetadataResource, srcContext, prerelease, reinstall));
                }
            }

            return foundDependencies;
        }
    }
}

