﻿using System;
using System.IO;
using System.Reflection;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.Win32;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using System.Security.Principal;

namespace MSBuilder
{
	/// <summary>
	/// Installs a VSIX to the given Visual Studio version and optional hive/instance (i.e. 'Exp').
	/// Allows specifying per-machine install too.
	/// </summary>
	public class InstallVsix : Task
	{
		/// <summary>
		/// Visual Studio version to install the VSIX to.
		/// </summary>
		[Required]
		public string VisualStudioVersion { get; set; }

		/// <summary>
		/// Full path to the VSIX file to install.
		/// </summary>
		[Required]
		public string VsixPath { get; set; }

		/// <summary>
		/// Optional flag to install the VSIX on the machine-wide location.
		/// </summary>
		public bool PerMachine { get; set; }

		/// <summary>
		/// Optional hive/instance to install to (i.e. 'Exp').
		/// </summary>
		public string RootSuffix { get; set; }

		/// <summary>
		/// Optional message importance for the task messages.
		/// </summary>
		public string MessageImportance { get; set; }

		/// <summary>
		/// Ensures the given VSIX is installed and enabled for the given 
		/// Visual Studio version and instance/hive.
		/// </summary>
		public override bool Execute()
		{
			string vsdir = null;
			using (var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
			using (var key = root.OpenSubKey(@"Software\Microsoft\VisualStudio\" + VisualStudioVersion))
			{
				if (key != null)
				{
					vsdir = key.GetValue("InstallDir") as string;
				}
				else
				{
					Log.LogError("Failed to locate installation directory for VisualStudioVersion '{0}'.", VisualStudioVersion);
					return false;
				}
			}

			var importance = Microsoft.Build.Framework.MessageImportance.Normal;
			if (!string.IsNullOrEmpty(MessageImportance))
				importance = (MessageImportance)Enum.Parse(typeof(MessageImportance), MessageImportance, true);

			var managerAsm = Assembly.LoadFrom(Path.Combine(vsdir, @"PrivateAssemblies\Microsoft.VisualStudio.ExtensionManager.Implementation.dll"));

			var vssdk = new DirectoryInfo(Path.Combine(vsdir, @"..\..\VSSDK\VisualStudioIntegration\Common\Assemblies\v4.0")).FullName;
			if (!Directory.Exists(vssdk))
				throw new ArgumentException("Visual Studio SDK was not found at expected path '" + vssdk + "'.");

			var settingsAsm = Assembly.LoadFrom(Path.Combine(vssdk, string.Format(@"Microsoft.VisualStudio.Settings.{0}.dll", VisualStudioVersion)));
			var settingsType = settingsAsm.GetType("Microsoft.VisualStudio.Settings.ExternalSettingsManager");
			var settings = settingsType.InvokeMember("CreateForApplication", BindingFlags.Static | BindingFlags.Public | BindingFlags.InvokeMethod, null, null,
				new[] { Path.Combine(vsdir, "devenv.exe"), RootSuffix ?? "" });

			var managerType = managerAsm.GetType("Microsoft.VisualStudio.ExtensionManager.ExtensionManagerService", true);
			var manager = Activator.CreateInstance(managerType, new[] { settings });

			object extension = managerType.InvokeMember("CreateInstallableExtension", BindingFlags.Static | BindingFlags.Public | BindingFlags.InvokeMethod, null, null, new[] { VsixPath });
			var header = extension.GetType().InvokeMember("Header", BindingFlags.GetProperty, null, extension, null);
			var id = (string)header.GetType().InvokeMember("Identifier", BindingFlags.GetProperty, null, header, null);
			var name = (string)header.GetType().InvokeMember("Name", BindingFlags.GetProperty, null, header, null);
			var newVersion = (Version)header.GetType().InvokeMember("Version", BindingFlags.GetProperty, null, header, null);
			var vsversion = "Visual Studio " + VisualStudioVersion;
			if (!string.IsNullOrEmpty(RootSuffix))
				vsversion += " (" + RootSuffix + ")";

			var isInstalled = (bool)managerType.InvokeMember("IsInstalled", BindingFlags.Instance | BindingFlags.Public | BindingFlags.InvokeMethod, null, manager, new[] { extension });
			var isInstalledPerMachine = false;
			if (isInstalled)
			{
				// If previously installed, uninstall first.
				var installedExtension = managerType.InvokeMember("GetInstalledExtension", BindingFlags.Instance | BindingFlags.Public | BindingFlags.InvokeMethod, null, manager, new[] { id });
				var installedHeader = installedExtension.GetType().InvokeMember("Header", BindingFlags.GetProperty, null, installedExtension, null);
				// SystemComponent can't be uninstalled via the API call.
				var isSystemComponent = (bool)installedHeader.GetType().InvokeMember("SystemComponent", BindingFlags.GetProperty, null, installedHeader, null);
				isInstalledPerMachine = (bool)installedExtension.GetType().InvokeMember("InstalledPerMachine", BindingFlags.GetProperty, null, installedExtension, null);
				var isAdministrator = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
				var oldVersion = (Version)installedHeader.GetType().InvokeMember("Version", BindingFlags.GetProperty, null, installedHeader, null);

				if (oldVersion == newVersion)
				{
					Log.LogMessage(importance, "Existing extension '{0}' (id={1}) version {2} found on {3} matches version to install. Assuming the existing extension is the right one.", name, id, newVersion, vsversion);
					return true;
				}
				if (oldVersion > newVersion)
				{
					Log.LogWarning("Existing extension '{0}' (id={1}) version {2} found on {3} is greater than the version {4} to install. Assuming the existing extension does not need downgrading.", name, id, oldVersion, vsversion, newVersion);
					return true;
				}

				if (!isSystemComponent)
				{
					if (isInstalledPerMachine && !isAdministrator)
					{
						Log.LogError("Existing extension '{0}' (id={1}) version {2} found on {3} does not match version {4} to install and is installed per-machine, but the current user isn't an Administrator and cannot uninstall it. You can manually uninstall it from Visual Studio Extension and Updates dialog, or run Visual Studio in elevated mode ('Run as administrator') to fix it.", 
							name, id, oldVersion, vsversion, newVersion);
						return false;
					}

					managerType.InvokeMember("Uninstall", BindingFlags.Instance | BindingFlags.Public | BindingFlags.InvokeMethod, null, manager, new[] { installedExtension });
					Log.LogMessage(importance, "Successfully uninstalled existing extension '{0}' (id={1}) version {2} found on {3}.", name, id, oldVersion, vsversion);
				}
				else
				{
					Log.LogError("Existing extension '{0}' (id={1}) version {2} found on {3} does not match version {4} to install. Since it is marked as a SystemComponent, it cannot be automatically uninstalled. It must be uninstalled using the same installer that installed it.", 
						name, id, oldVersion, vsversion, newVersion);
					return false;
				}

				if (!isInstalledPerMachine)
				{
					// Clear existing extension's install directory to avoid MEF corruption on restart
					var xmlns = new XmlNamespaceManager(new NameTable());
					xmlns.AddNamespace("v1", "http://schemas.microsoft.com/developer/vsx-schema/2010");
					xmlns.AddNamespace("v2", "http://schemas.microsoft.com/developer/vsx-schema/2011");

					var extensionsDir = Path.Combine(
						Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
						"Microsoft", "VisualStudio",
						VisualStudioVersion + RootSuffix,
						"Extensions");
					if (Directory.Exists(extensionsDir))
					{
						var vsixDirDeleted = false;
						foreach (var manifest in Directory.EnumerateFiles(extensionsDir, "extension.vsixmanifest", SearchOption.AllDirectories))
						{
							var vsixDoc = XDocument.Load(manifest);
							var vsixId = (string)vsixDoc.XPathEvaluate("string(/v1:Vsix/v1:Identifier/@Id)", xmlns);
							if (string.IsNullOrEmpty(vsixId))
								vsixId = (string)vsixDoc.XPathEvaluate("string(/v2:PackageManifest/v2:Metadata/v2:Identity/@Id)", xmlns);

							if (vsixId == id)
							{
								var vsixDir = new FileInfo(manifest).Directory.FullName;
								try
								{
									Directory.Delete(vsixDir, true);
									vsixDirDeleted = true;
									Log.LogMessage(importance, "Succesfully deleted existing extension folder '{0}'.", vsixDir);
								}
								catch
								{
									Log.LogWarning("Failed to delete existing extension folder '{0}'.", vsixDir);
								}
							}
						}

						// If we deleted the directory, we must re-create the manager and extension, since 
						// otherwise the cached list of installed extensions will try to access the deleted 
						// manifest when we install, causing an exception.
						if (vsixDirDeleted)
						{
							// We also need to delete the extension.[locale].cache file since we want the list updated now
							foreach (var cacheFile in Directory.EnumerateFiles(extensionsDir, "extensions.*.cache").ToArray())
							{
								try
								{
									File.Delete(cacheFile);
									Log.LogMessage(importance, "Successfully deleted existing extensions cache file '{0}'.", cacheFile);
								}
								catch
								{
									Log.LogWarning("Failed to delete existing extensions cache file '{0}'.", cacheFile);
								}
							}

							manager = Activator.CreateInstance(managerType, new[] { settings });
							extension = managerType.InvokeMember("CreateInstallableExtension", BindingFlags.Static | BindingFlags.Public | BindingFlags.InvokeMethod, null, null, new[] { VsixPath });
						}
					}
				}
			}

			try
			{
				Log.LogMessage(importance, "Installing '{0}' (id={1}) version {2} on {3}.", name, id, newVersion, vsversion);
				managerType.InvokeMember("Install", BindingFlags.Instance | BindingFlags.Public | BindingFlags.InvokeMethod, null, manager, new[] { extension, PerMachine });
				managerType.InvokeMember("UpdateLastExtensionsChange", BindingFlags.Instance | BindingFlags.Public | BindingFlags.InvokeMethod, null, manager, new object[0]);
				Log.LogMessage(importance, "Successfully installed extension '{0}' (id={1}) version {2} on {3}.", name, id, newVersion, vsversion);

			}
			catch (TargetInvocationException tie)
			{
				var ex = tie.GetBaseException();
				if (ex.GetType().Name == "BreaksExistingExtensionsException")
				{
					var message = ex.Message;
					message = message.Substring(message.IndexOf(':') + 1);
					var impacted = message
						.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries)
						.Select(s => s.Replace("-", "").Trim());
					message = string.Join(", ", impacted);
					Log.LogError(ex.Message.Substring(0, ex.Message.IndexOf(':')) + ": " + message);
					Log.LogMessage(importance, "You can uninstall the impacted extensions by running the build again with the following arguments: ");
					foreach (var ie in impacted)
					{
						Log.LogMessage(importance, "\t /t:UninstallVsix /p:VsixId={0}", ie);
					}
				}
				else
				{
					Log.LogErrorFromException(ex, true);
				}

				return false;
			}

			return true;
		}
	}
}
