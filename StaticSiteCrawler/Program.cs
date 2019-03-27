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
        private static HttpClient client = new HttpClient();

        private static HashSet<string> doneUrls;
        private static HashSet<string> failedUrls;
        private static bool urlListOnly = false;
        private static bool downloadMissingOnly = false;

        private static class Parameters
        {
            public const string DownloadMissingOnly = "--downloadmissing";
            public const string UrlOnly = "--urlonly";
        }

        private static void Log(string message)
        {
            if (!urlListOnly)
                Console.WriteLine(message);
        }


        static async Task Main(string[] args)
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
                Console.WriteLine($"- Use '{Parameters.UrlOnly}' to print URL list only.");
                Console.WriteLine($"- Use '{Parameters.DownloadMissingOnly}' to download only files missing in the output directory.");
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
                    string argNormalized = args[i].ToLowerInvariant();
                    if (argNormalized == Parameters.UrlOnly)
                        urlListOnly = true;
                    else if (argNormalized == Parameters.DownloadMissingOnly)
                        downloadMissingOnly = true;
                    else
                        startUrls.Add(args[i]);
                }
            }

            EnsureDirectory(outputPath);

            if (downloadMissingOnly)
                Log("Downloading only not existing file.");

            Log($"Crawling '{url}'.");

            doneUrls = new HashSet<string>();
            failedUrls = new HashSet<string>();

            foreach (string startUrl in startUrls)
            {
                string urlToExecute = CombineUrl(url, startUrl);
                await ExecuteAsync(url, urlToExecute, outputPath);
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

            string outputFilePath = GetOutputFilePath(outputPath, urlToExecute.Substring(rootUrl.Length));
            if (downloadMissingOnly && File.Exists(outputFilePath))
            {
                Log($"Skipping '{urlToExecute}' because local file already exists.");
                return;
            }

            var content = await GetUrlContentAsync(urlToExecute);
            doneUrls.Add(urlToExecute);

            if (content != null)
            {
                if (!urlListOnly)
                    await SaveContentAsync(outputFilePath, content.Value.body);

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

                UriBuilder uriBuilder = new UriBuilder(uri);
                uriBuilder.Fragment = null;

                if (uriBuilder.Query == "?")
                    uriBuilder.Query = null;

                url = new Uri(uriBuilder.ToString()).ToString();
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

        private readonly static List<string> fileExtensions = new List<string>()
        {
            ".html",
            ".xml",
            ".js",
            ".css",
            ".jpg",
            ".png",
            ".ico",
            ".gif",
            ".svg",
            ".eot",
            ".ttf",
            ".woff"
        };

        private static async Task SaveContentAsync(string file, HttpContent content)
        {
            File.WriteAllBytes(file, await content.ReadAsByteArrayAsync());
        }

        private static string GetOutputFilePath(string outputPath, string path)
        {
            string targetDirectory;
            string file;

            if (path.Length > 0 && (path[0] == Path.DirectorySeparatorChar || path[0] == Path.AltDirectorySeparatorChar))
                path = path.Substring(1);

            if (fileExtensions.Any(e => path.EndsWith(e, StringComparison.InvariantCultureIgnoreCase)))
            {
                Log($"Getting directory name from '{path}'.");
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
            return file;
        }

        private static async Task<(HttpContent body, string contentType)?> GetUrlContentAsync(string url)
        {
            HttpResponseMessage response = await client.GetAsync(url);
            Log($"URL '{url}' returned with code '{(int)response.StatusCode}'.");
            if (response.StatusCode == HttpStatusCode.OK)
                return (response.Content, response.Content.Headers.ContentType.MediaType);


            failedUrls.Add(url);
            return null;
        }

        private const RegexOptions regexOptions = RegexOptions.IgnoreCase | RegexOptions.Compiled;

        private readonly static List<Regex> htmlRegexes = new List<Regex>()
        {
            new Regex("<a.*?(?<attribute>href|name)=\"(?<value>.*?)\".*?>", regexOptions), // HTML a href
            new Regex("<img.*?(?<attribute>src)=\"(?<value>.*?)\".*?>", regexOptions), // HTML img src
            new Regex("<script.*?(?<attribute>src)=\"(?<value>.*?)\".*?>", regexOptions), // HTML script src
            new Regex("<link.*?(?<attribute>href)=\"(?<value>(.*\\.css)?)\".*?>", regexOptions), // HTML link href *.css
            new Regex("<link.*?(?<attribute>href)=\"(?<value>(.*\\.ico)?)\".*?>", regexOptions) // HTML link href *.ico - favicon
        };

        private readonly static List<Regex> cssRegexes = new List<Regex>()
        {
            new Regex("url\\(\"(?<value>.*?)\"\\)", regexOptions) // CSS backgroun url()
        };

        private static async Task<List<string>> GetLinksAsync((HttpContent body, string contentType) content)
        {
            List<string> result = new List<string>();

            string body = await content.body.ReadAsStringAsync();
            if (String.IsNullOrEmpty(body))
                return result;

            List<Regex> regexes = FindContentRegexesByContentType(content.contentType);
            if (regexes == null)
                return result;

            foreach (Regex regex in regexes)
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

        private static List<Regex> FindContentRegexesByContentType(string contentType)
        {
            if (contentType == "text/css")
                return cssRegexes;
            else if (contentType == "text/html")
                return htmlRegexes;

            return null;
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
