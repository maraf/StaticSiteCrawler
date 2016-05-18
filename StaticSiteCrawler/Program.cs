using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace StaticSiteCrawler
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length != 2)
            {
                Console.WriteLine("Incorrect usage. The application requires 2 arguments:");
                Console.WriteLine("- The URL to crawl.");
                Console.WriteLine("- The output directory.");
                return;
            }

            string url = args[0];
            string outputPath = args[1];
            EnsureDirectory(outputPath);

            Console.WriteLine("Crawling {0}...", url);

            HashSet<string> doneUrls = new HashSet<string>();
            Execute(doneUrls, url, url, outputPath).Wait();

            Console.WriteLine("Done.");
#if DEBUG
            Console.ReadKey(true);
#endif
        }

        private static async Task Execute(HashSet<string> doneUrls, string rootUrl, string urlToExecute, string outputPath)
        {
            string content = await GetUrlContent(urlToExecute);
            doneUrls.Add(urlToExecute);

            SaveContent(outputPath, urlToExecute.Substring(rootUrl.Length), content);

            List<string> links = GetLinks(content);
            foreach (string link in links)
            {
                string url = link;
                if (url.StartsWith("/"))
                    url = CombineUrl(rootUrl, link);

                if (url.StartsWith(rootUrl) && !doneUrls.Contains(url))
                    await Execute(doneUrls, rootUrl, url, outputPath);
            }
        }

        private static void SaveContent(string outputPath, string path, string content)
        {
            if (path.EndsWith(".html"))
            {
                // TODO: Save as file
            }
            else
            {
                string targetDirectory = Path.Combine(outputPath, path);
                EnsureDirectory(targetDirectory);

                string file = Path.Combine(targetDirectory, "index.html");
                File.WriteAllText(file, content);
            }
        }

        private static async Task<string> GetUrlContent(string url)
        {
            using (HttpClient client = new HttpClient())
            {
                HttpResponseMessage response = await client.GetAsync(url);
                if (response.StatusCode == HttpStatusCode.OK)
                    return await response.Content.ReadAsStringAsync();
            }

            return null;
        }

        private static Regex linkRegex = new Regex("<a.*?(?<attribute>href|name)=\"(?<value>.*?)\".*?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static List<string> GetLinks(string content)
        {
            List<string> result = new List<string>();
            MatchCollection matches = linkRegex.Matches(content);
            foreach (Match match in matches)
            {
                string path = match.Groups["value"].Value;
                result.Add(path);
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
            else if(!rootUrl.EndsWith("/") && !path.StartsWith("/"))
                path = "/" + path;

            return rootUrl + path;
        }
    }
}
