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
                    startUrls.Add(args[i]);
            }

            EnsureDirectory(outputPath);

            Console.WriteLine("Crawling {0}...", url);

            doneUrls = new HashSet<string>();
            failedUrls = new HashSet<string>();

            foreach (string startUrl in startUrls)
            {
                string urlToExecute = CombineUrl(url, startUrl);
                ExecuteAsync(url, urlToExecute, outputPath).Wait();
            }

            if (failedUrls.Count > 0)
                Environment.ExitCode = 1;

            Console.WriteLine($"Done. Processed '{doneUrls.Count}' URLs. Failed '{failedUrls.Count}' URLs.");

#if DEBUG
            Console.ReadKey(true);
#endif
        }

        private static bool TrustToAllCertifacatesCallback(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors) => true;

        private static async Task ExecuteAsync(string rootUrl, string urlToExecute, string outputPath)
        {
            Console.WriteLine("Processing URL '{0}'.", urlToExecute);
            string content = await GetUrlContentAsync(urlToExecute);
            doneUrls.Add(urlToExecute);

            if (!String.IsNullOrEmpty(content))
            {

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
            Console.WriteLine("Writing file '{0}'.", file);
            File.WriteAllText(file, content);
        }

        private static async Task<string> GetUrlContentAsync(string url)
        {
            using (HttpClient client = new HttpClient())
            {
                HttpResponseMessage response = await client.GetAsync(url);
                Console.WriteLine($"URL '{url}' returned with code '{(int)response.StatusCode}'.");
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
                Console.WriteLine("Creating directory '{0}'.", path);
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
