using System;
using System.Linq;
using System.Text;
using System.IO;
using System.Globalization;

namespace ResourceCompiler
{
	class Program
	{
		private static string programFilesx86
		{
			get
			{
				if (Environment.Is64BitOperatingSystem)
					return Environment.GetEnvironmentVariable("ProgramFiles(x86)");
				return Environment.GetEnvironmentVariable("ProgramFiles");
			}
		}

		private static readonly string sdkPath = Path.Combine(new string[] { programFilesx86, "Microsoft SDKs", "Windows", "v8.0A", "bin", "NETFX 4.0 Tools" });

		private static readonly string resgenPath = Path.Combine(sdkPath, "ResGen.exe");
		private static readonly string alPath = Path.Combine(sdkPath, "al.exe");
		private static readonly string cscPath = Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "csc.exe");

		private static bool resourceMainAtRoot = true;
		private static string resourceNamespace = "MyResources";
		private static string resourcePath = "Resources";
		private static string resourceSourcePath = null;

		static void Main(string[] args)
		{
			// check paths and files.
			if (!checkPaths())
				return;

			// get settings from the config file.
			try
			{
				var config = System.Configuration.ConfigurationManager.AppSettings;

				configTryGet(config, "ResourceNamespace", ref resourceNamespace);
				configTryGet(config, "ResourcePath", ref resourcePath);
				configTryGet(config, "ResourceSourcePath", ref resourceSourcePath);
			}
			catch (System.Configuration.ConfigurationErrorsException)
			{
			}

			// if no resource source path spesified default to the resource path.
			if (resourceSourcePath == null || resourceSourcePath.Length == 0)
				resourceSourcePath = resourcePath;

			Directory.CreateDirectory(resourcePath);

			string[] files;


			// Generate resources for default embedded resources.
			Console.WriteLine("\nGenerating default resources...\n");
			files = getRawResourcesFiles(resourceSourcePath, SearchOption.TopDirectoryOnly);
			if (files.Length == 0)
			{
				Console.WriteLine("No default resource sources found!");
				waitAnyKey();
				return;
			}
			foreach (var file in files)
			{
				var name = Path.GetFileNameWithoutExtension(file);

				var fileNamespace = resourceNamespace;
				var names = name.Split('.');
				if (names.Length > 1)
				{
					fileNamespace = resourceNamespace + "." + string.Join(".", names.Take(names.Length - 1));
					name = names[names.Length - 1];
				}

				var outfile = Path.Combine(resourcePath, fileNamespace + "." + name + ".resources");

				var classPath = Path.Combine(resourcePath, name + ".cs");
				var classinfo = "/str:cs," + string.Join(",", fileNamespace, name, classPath);

				exec(resgenPath, string.Join(" ", file, outfile, classinfo, "/publicClass"));
			}


			// Compile helper classes and embed default resources in to lib.
			Console.WriteLine("\nCompiling default resources...\n");
			{
				files = Directory.GetFiles(resourcePath, "*.resources", SearchOption.TopDirectoryOnly);
				if (files.Length == 0)
				{
					waitAnyKey("Unable to continue.");
					return;
				}

				var arguments = new StringBuilder();
				arguments.Append("/target:library");
				if (resourceMainAtRoot)
					arguments.Append(" /out:" + resourceNamespace + ".dll");
				else
					arguments.Append(" /out:" + Path.Combine(resourcePath, resourceNamespace + ".dll"));
				foreach (var file in files)
					arguments.Append(" /res:" + file);
				arguments.Append(" " + Path.Combine(resourcePath, "*.cs"));

				exec(cscPath, arguments.ToString());

				Console.WriteLine("Deleting temp files...");
				// delete .resources files
				foreach (var file in files)
					File.Delete(file);
				// delete .cs files
				files = Directory.GetFiles(resourcePath, "*.cs");
				foreach (var file in files)
					File.Delete(file);
			}

			// Generate resources for soecific cultures.
			var cultureDirectories = Directory.GetDirectories(resourceSourcePath, "*", SearchOption.TopDirectoryOnly);
			foreach (var cultureDirectory in cultureDirectories)
			{
				string name = Path.GetFileName(cultureDirectory);
				try
				{
					// get culture
					var culture = CultureInfo.GetCultureInfo(name);
					var outPath = Path.Combine(resourcePath, culture.Name);
					Directory.CreateDirectory(outPath);

					bool empty = true;
					if (generateResourceLib(cultureDirectory, outPath, resourceNamespace, culture))
						empty = false;

					// use culture sub directories as other assemblies.
					var subDir = Directory.GetDirectories(cultureDirectory, "*", SearchOption.TopDirectoryOnly);
					foreach (var dir in subDir)
						if (generateResourceLib(dir, outPath, Path.GetFileName(dir), culture))
							empty = false;

					if (empty)
						Console.WriteLine("Culture \"" + culture.Name + "\" had no resources!");
				}
				catch (CultureNotFoundException)
				{
					Console.WriteLine("Error: \"" + name + "\" is not a valid culture name!");
				}
			}

			// end
			Console.WriteLine("\nFinished.");
			waitAnyKey();
		}

		private static bool checkPaths()
		{
			if (!Directory.Exists(sdkPath))
				writeFail("Windows SDK for .Net Framework 4 was not found!\n" + sdkPath + "\nDownload from:\n" + "https://www.microsoft.com/en-us/download/details.aspx?id=8279");
			else if (!File.Exists(resgenPath))
				writeFail("ResGen.exe was not found in: " + resgenPath);
			else if (!File.Exists(cscPath))
				writeFail("ssc.exe was not found in: " + cscPath);
			else if (!File.Exists(alPath))
				writeFail("al.exe was not found in: " + alPath);
			else
				return true;
			return false;
		}

		private static bool generateResourceLib(string path, string outPath, string libNamespace, CultureInfo culture)
		{
			// generate resources
			var files = getRawResourcesFiles(path, SearchOption.TopDirectoryOnly);
			if (files.Length == 0)
				return false;
			else
				Console.WriteLine("\nGenerating resources for culture \"" + culture.Name + "\"...");
			foreach (var file in files)
			{
				var outfile = Path.Combine(outPath, libNamespace + "." + Path.GetFileNameWithoutExtension(file) + "." + culture.Name + ".resources");
				exec(resgenPath, file + " " + outfile);
			}

			// link resources to lib
			files = Directory.GetFiles(outPath, "*.resources", SearchOption.TopDirectoryOnly);
			if (files.Length == 0)
				return false;

			var arguments = new StringBuilder();
			arguments.Append("/target:lib /culture:");
			arguments.Append(culture.Name);
			arguments.Append(" /out:");
			arguments.Append(Path.Combine(outPath, libNamespace + ".resources.dll"));
			foreach (var file in files)
			{
				arguments.Append(" /embed:");
				arguments.Append(file);
			}
			exec(alPath, arguments.ToString());
			Console.WriteLine("Delete temp resource files..");
			foreach (var file in files)
				File.Delete(file);
			return true;
		}

		/// <summary> Gets all files with .txt .restext .resx extensions.  </summary>
		private static string[] getRawResourcesFiles(string path, SearchOption searchOption)
		{
			return Directory.GetFiles(path, "*.*", searchOption).Where(file =>
				file.EndsWith(".txt", StringComparison.InvariantCultureIgnoreCase) ||
				file.EndsWith(".restext", StringComparison.InvariantCultureIgnoreCase) ||
				file.EndsWith(".resx", StringComparison.InvariantCultureIgnoreCase)
			).ToArray();
		}

		/// <summary> Executes process without window and dumps output to console. </summary>
		private static void exec(string fileName, string arguments)
		{
			var process = new System.Diagnostics.Process();
			var startinfo = new System.Diagnostics.ProcessStartInfo();

			startinfo.FileName = fileName;
			startinfo.Arguments = arguments;

			startinfo.CreateNoWindow = true;
			startinfo.UseShellExecute = false;
			startinfo.RedirectStandardOutput = true;

			process.StartInfo = startinfo;
			process.Start();

			while (!process.HasExited)
			{
				Console.Out.Write(process.StandardOutput.ReadToEnd());
				System.Threading.Thread.Sleep(50);
			}
			Console.Out.Write(process.StandardOutput.ReadToEnd());
		}

		private static void writeFail(string str)
		{
			Console.WriteLine();
			Console.WriteLine("Error:");
			Console.WriteLine(str);
			waitAnyKey();
		}
		private static void waitAnyKey(string str = null)
		{
			if (str != null)
				Console.WriteLine("\n" + str);
			Console.WriteLine();
			Console.WriteLine("Press any key to exit.");
			Console.ReadKey();
		}
		/// <summary> Only sets value if key is found and not empty. </summary>
		private static bool configTryGet(System.Collections.Specialized.NameValueCollection config, string key, ref string value)
		{
			string v = config[key];
			if (v == null || v.Length == 0)
				return false;
			value = v;
			return true;
		}
	}
}
