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
            //Console.ReadKey(true);
#endif
        }

        private static bool TrustToAllCertifacatesCallback(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors) => true;

        private static async Task ExecuteAsync(string rootUrl, string urlToExecute, string outputPath)
        {
            Log($"Processing URL '{urlToExecute}'.");
            var content = await GetUrlContentAsync(urlToExecute);
            doneUrls.Add(urlToExecute);

            if (content != null)
            {
                if (!urlListOnly)
                    await SaveContentAsync(outputPath, urlToExecute.Substring(rootUrl.Length), content.Value.body);

                List<string> links = await GetLinksAsync(content.Value);
                PrepareUrls(urlToExecute, links);
                await ProcessLinksAsync(rootUrl, outputPath, links);
            }
        }

        private static void PrepareUrls(string urlToExecute, List<string> links)
        {
            Uri uriToExecute = new Uri(urlToExecute);
            for (int i = 0; i < links.Count; i++)
            {
                string url = links[i];

                Uri uri = new Uri(uriToExecute, url);
                url = uri.ToString();
                links[i] = url;
            }
        }

        private static async Task ProcessLinksAsync(string rootUrl, string outputPath, List<string> links)
        {
            foreach (string link in links)
            {
                if (link.StartsWith(rootUrl) && !doneUrls.Contains(link))
                    await ExecuteAsync(rootUrl, link, outputPath);
            }
        }

        private readonly static List<string> fileExtensions = new List<string>() { ".html", ".xml", ".js", ".css", ".jpg", ".png", ".gif", ".svg" };
        private readonly static List<Regex> textContentTypes = new List<Regex>() { new Regex("text/(.*)"), new Regex("application/xml"), new Regex("application/json"), new Regex("application/javascript") };

        private static async Task SaveContentAsync(string outputPath, string path, HttpContent content)
        {
            string targetDirectory;
            string file;

            if (path.Length > 0 && (path[0] == Path.DirectorySeparatorChar || path[0] == Path.AltDirectorySeparatorChar))
                path = path.Substring(1);

            if (fileExtensions.Any(e => path.EndsWith(e, StringComparison.InvariantCultureIgnoreCase)))
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

            File.WriteAllBytes(file, await content.ReadAsByteArrayAsync());
        }

        private static async Task<(HttpContent body, string contentType)?> GetUrlContentAsync(string url)
        {
            using (HttpClient client = new HttpClient())
            {
                HttpResponseMessage response = await client.GetAsync(url);
                Log($"URL '{url}' returned with code '{(int)response.StatusCode}'.");
                if (response.StatusCode == HttpStatusCode.OK)
                    return (response.Content, response.Content.Headers.ContentType.MediaType);

            }

            failedUrls.Add(url);
            return null;
        }

        private readonly static Regex htmlLinkRegex = new Regex("<a.*?(?<attribute>href|name)=\"(?<value>.*?)\".*?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private readonly static Regex htmlImageRegex = new Regex("<img.*?(?<attribute>src)=\"(?<value>.*?)\".*?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private readonly static Regex htmlScriptRegex = new Regex("<script.*?(?<attribute>src)=\"(?<value>.*?)\".*?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private readonly static Regex htmlLinkStyleRegex = new Regex("<link.*?(?<attribute>href)=\"(?<value>(.*\\.css)?)\".*?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private readonly static List<Regex> htmlRegexes = new List<Regex>() { htmlLinkRegex, htmlImageRegex, htmlScriptRegex, htmlLinkStyleRegex };

        private readonly static Regex cssBackgroundRegex = new Regex("url\\(\"(?<value>.*?)\"\\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private readonly static List<Regex> cssRegexes = new List<Regex>() { cssBackgroundRegex };

        private static async Task<List<string>> GetLinksAsync((HttpContent body, string contentType) content)
        {
            List<string> result = new List<string>();
            if (!textContentTypes.Any(r => r.IsMatch(content.contentType)))
                return result;

            string body = await content.body.ReadAsStringAsync();
            if (String.IsNullOrEmpty(body))
                return result;

            foreach (Regex regex in content.contentType == "text/css" ? cssRegexes : htmlRegexes)
            {
                MatchCollection matches = regex.Matches(body);
                foreach (Match match in matches)
                {
                    if (match != null && match.Groups != null && match.Groups["value"] != null)
                    {
                        string path = match.Groups["value"].Value;
                        result.Add(path);
                    }
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
