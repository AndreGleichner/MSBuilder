﻿using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Xunit;
using System.Linq;
using System.Xml.Linq;
using System.Collections.Generic;

namespace MSBuilder
{
	public class AddVsixDependencySpec
	{
		static readonly XNamespace xmlns = XNamespace.Get("http://schemas.microsoft.com/developer/vsx-schema/2011");

		[Fact]
		public void when_injecting_non_existent_dependency_then_adds_dependency()
		{
			var targetVsix = Path.GetTempFileName();
			File.Copy("source.extension.vsixmanifest", targetVsix, true);

			var task = new AddVsixDependency
			{
				BuildEngine = new MockBuildEngine(),
				TargetVsixManifest = new TaskItem(targetVsix),
				VsixDependencyManifest = new []
				{
					new TaskItem("clide.v1.vsixmanifest", new Dictionary<string, string> { { "VsixPath", "Clide.vsix" } })
				}
			};

			Assert.True(task.Execute());

			var clide = XDocument.Load(targetVsix).Root.Element(xmlns + "Dependencies")?
				.Elements(xmlns + "Dependency")
				.FirstOrDefault(e => e.Attribute("Id")?.Value == "Clide");

			Assert.NotNull(clide);
		}

		[Fact]
		public void when_updating_existing_dependency_then_updates_version()
		{
			var targetVsix = Path.GetTempFileName();
			File.Copy("merqed.extension.vsixmanifest", targetVsix, true);

			var task = new AddVsixDependency
			{
				BuildEngine = new MockBuildEngine(),
				TargetVsixManifest = new TaskItem(targetVsix),
				VsixDependencyManifest = new[]
				{
					new TaskItem("merq.v2.vsixmanifest", new Dictionary<string, string> { { "VsixPath", "Merq.vsix" } })
				}
			};

			Assert.True(task.Execute());

			var clide = XDocument.Load(targetVsix).Root.Element(xmlns + "Dependencies")?
				.Elements(xmlns + "Dependency")
				.First(e => e.Attribute("Id")?.Value == "Merq");

			Assert.True(clide.Attribute("Version")?.Value.StartsWith("[2.0"));
		}
	}
}
