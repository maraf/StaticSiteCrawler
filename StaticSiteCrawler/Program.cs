using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace StaticSiteCrawler
{
    class Program
    {
        private static HashSet<string> doneUrls;
        private static HashSet<string> failedUrls;
        private static bool urlListOnly = false;

        private static void Log(string message)
        {
            if (!urlListOnly)
                Console.WriteLine(message);
        }

        static void Main(string[] args)
        {
            ServicePointManager.Expect100Continue = true;
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            ServicePointManager.ServerCertificateValidationCallback = TrustToAllCertifacatesCallback;

            if (args.Length <= 2)
            {
                Console.WriteLine("Incorrect usage. The application requires 2 arguments:");
                Console.WriteLine("- The URL to crawl.");
                Console.WriteLine("- The output directory.");
                Console.WriteLine("- (optional) The semicolon separated list of root paths to start crawling with (eg.: /;/404.html).");
                Console.WriteLine("- Use '--urlonly' to print URL list only.");
                return;
            }

            string url = args[0];
            string outputPath = args[1];

            HashSet<string> startUrls = new HashSet<string>();
            if (args.Length == 2)
            {
                startUrls.Add("/");
            }
            else
            {
                for (int i = 2; i < args.Length; i++)
                {
                    if (args[i].ToLowerInvariant() == "--urlonly")
                        urlListOnly = true;
                    else
                        startUrls.Add(args[i]);
                }
            }

            EnsureDirectory(outputPath);

            Log($"Crawling {url}...");

            doneUrls = new HashSet<string>();
            failedUrls = new HashSet<string>();

            foreach (string startUrl in startUrls)
            {
                string urlToExecute = CombineUrl(url, startUrl);
                ExecuteAsync(url, urlToExecute, outputPath).Wait();
            }

            if (urlListOnly)
            {
                foreach (string item in doneUrls)
                    Console.WriteLine(item);
            }

            if (failedUrls.Count > 0)
                Environment.ExitCode = 1;

            Log($"Done. Processed '{doneUrls.Count}' URLs. Failed '{failedUrls.Count}' URLs.");

#if DEBUG
            Console.ReadKey(true);
#endif
        }

        private static bool TrustToAllCertifacatesCallback(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors) => true;

        private static async Task ExecuteAsync(string rootUrl, string urlToExecute, string outputPath)
        {
            Log($"Processing URL '{urlToExecute}'.");
            string content = await GetUrlContentAsync(urlToExecute);
            doneUrls.Add(urlToExecute);

            if (!String.IsNullOrEmpty(content))
            {
                if (!urlListOnly)
                    SaveContent(outputPath, urlToExecute.Substring(rootUrl.Length), content);

                List<string> links = GetLinks(content);
                await ProcessLinksAsync(rootUrl, outputPath, links);
            }
        }

        private static async Task ProcessLinksAsync(string rootUrl, string outputPath, List<string> links)
        {
            foreach (string link in links)
            {
                string url = link;
                if (url.StartsWith("/"))
                    url = CombineUrl(rootUrl, link);

                if (url.StartsWith(rootUrl) && !doneUrls.Contains(url))
                    await ExecuteAsync(rootUrl, url, outputPath);
            }
        }

        private static void SaveContent(string outputPath, string path, string content)
        {
            string targetDirectory;
            string file;

            if (path.Length > 0 && (path[0] == Path.DirectorySeparatorChar || path[0] == Path.AltDirectorySeparatorChar))
                path = path.Substring(1);

            if (path.EndsWith(".html") || path.EndsWith(".xml"))
            {
                targetDirectory = Path.Combine(outputPath, Path.GetDirectoryName(path));
                file = Path.Combine(targetDirectory, Path.GetFileName(path));
            }
            else
            {
                targetDirectory = Path.Combine(outputPath, path);
                file = Path.Combine(targetDirectory, "index.html");
            }

            EnsureDirectory(targetDirectory);
            Log($"Writing file '{file}'.");
            File.WriteAllText(file, content);
        }

        private static async Task<string> GetUrlContentAsync(string url)
        {
            using (HttpClient client = new HttpClient())
            {
                HttpResponseMessage response = await client.GetAsync(url);
                Log($"URL '{url}' returned with code '{(int)response.StatusCode}'.");
                if (response.StatusCode == HttpStatusCode.OK)
                    return await response.Content.ReadAsStringAsync();

            }

            failedUrls.Add(url);
            return null;
        }

        private static Regex linkRegex = new Regex("<a.*?(?<attribute>href|name)=\"(?<value>.*?)\".*?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static List<string> GetLinks(string content)
        {
            List<string> result = new List<string>();
            if (String.IsNullOrEmpty(content))
                return result;

            MatchCollection matches = linkRegex.Matches(content);
            foreach (Match match in matches)
            {
                if (match != null && match.Groups != null && match.Groups["value"] != null)
                {
                    string path = match.Groups["value"].Value;
                    result.Add(path);
                }
            }

            return result;
        }

        private static void EnsureDirectory(string path)
        {
            if (!Directory.Exists(path))
            {
                Log($"Creating directory '{path}'.");
                Directory.CreateDirectory(path);
            }
        }

        private static string CombineUrl(string rootUrl, string path)
        {
            if (rootUrl.EndsWith("/") && path.StartsWith("/"))
                path = path.Substring(1);
            else if (!rootUrl.EndsWith("/") && !path.StartsWith("/"))
                path = "/" + path;

            return rootUrl + path;
        }
    }
}
