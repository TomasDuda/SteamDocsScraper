using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using TidyManaged;

namespace SteamDocsScraper
{
    static class Program
    {
        private static string _docsDirectory;
        private static string _imgsDirectory;
        private static bool _signedIn;
        private static int _loginTries;

        // Key is the URL, value is if it was already fetched.
        private static readonly Dictionary<string, bool> DocumentationLinks = new Dictionary<string, bool>();
        private static Dictionary<string, string> _settings;
        private static ChromeDriver _chromeDriver;
        private static Regex _linkMatch;

        private static void Main()
        {
            Console.ResetColor();
            Console.Title = "Steam Documentation Scraper";

            _docsDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "docs");

            if (!File.Exists("settings.json"))
            {
                throw new Exception("settings.json file doesn't exist.");
            }

            _settings = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText("settings.json"));

            if (string.IsNullOrWhiteSpace(_settings["steamUsername"]) || string.IsNullOrWhiteSpace(_settings["steamPassword"]))
            {
                throw new Exception("Please provide your Steam username and password in settings.json.");
            }

            var options = new ChromeOptions();
            options.AddArgument($"--user-data-dir={Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "userdata")}");
            options.AddArgument("--enable-file-cookies");
            options.AddArgument("--disable-cache");

            _linkMatch = new Regex(@"//partner\.steamgames\.com/doc/(?<href>.+?)(?=#|\?|$)", RegexOptions.Compiled);

            _imgsDirectory = Path.Combine(_docsDirectory, "images");

            if (Directory.Exists(_docsDirectory))
            {
                Console.WriteLine($"Deleting existing folder: {_docsDirectory}");
                Directory.Delete(_docsDirectory, true);
            }

            Directory.CreateDirectory(_docsDirectory);
            Directory.CreateDirectory(_imgsDirectory);

            try
            {
                _chromeDriver = new ChromeDriver(options);

                Console.CancelKeyPress += delegate { _chromeDriver.Quit(); };

                _chromeDriver.Navigate().GoToUrl("https://partner.steamgames.com/");

                if (_chromeDriver.ElementIsPresent(By.ClassName("avatar")))
                {
                    _signedIn = true;
                }
                else
                {
                    Login();
                }

                if (_signedIn)
                {
                    if (_settings.Keys.Contains("predefinedDocs"))
                    {
                        foreach (var key in _settings["predefinedDocs"].Split(','))
                        {
                            if (string.IsNullOrWhiteSpace(key) || DocumentationLinks.ContainsKey(key))
                            {
                                Console.WriteLine("Invalid or duplicate predefined doc: {0}", key);
                                continue;
                            }

                            DocumentationLinks.Add(key, false);
                        }
                    }

                    _chromeDriver.Navigate().GoToUrl("https://partner.steamgames.com/doc/home");

                    GetDocumentationLinks();

                    AddFromSearchResults();

                    FetchLinks();
                }
            }
            finally
            {
                _chromeDriver?.Quit();
            }

            _settings["predefinedDocs"] = string.Join(",", DocumentationLinks.Keys.OrderBy(x => x));

            File.WriteAllText("settings.json", JsonConvert.SerializeObject(_settings, Formatting.Indented));

            Console.WriteLine("Done.");
            Console.ReadKey();
        }

        private static void Login()
        {
            new WebDriverWait(_chromeDriver, TimeSpan.FromSeconds(10)).Until(condition =>
            {
                try
                {
                    return _chromeDriver.FindElement(By.Id("login_btn_signin")).Displayed;
                }
                catch
                {
                    return false;
                }
            });

            var needsSteamGuard = _chromeDriver.ElementIsPresent(By.Id("authcode"));

            if (needsSteamGuard)
            {
                var friendlyName = _chromeDriver.FindElementById("friendlyname");
                friendlyName.SendKeys("SteamDocsScraper");

                var fieldEmailAuth = _chromeDriver.FindElementById("authcode");
                fieldEmailAuth.Clear();

                Console.Write("Please insert a Steam Guard code: ");
                var steamGuard = Console.ReadLine();
                fieldEmailAuth.SendKeys(steamGuard);

                var submitButton = _chromeDriver.FindElementByCssSelector("#auth_buttonsets .auth_button");
                submitButton.Click();
            }
            else
            {
                var fieldAccountName = _chromeDriver.FindElementById("steamAccountName");
                var fieldSteamPassword = _chromeDriver.FindElementById("steamPassword");
                var buttonLogin = _chromeDriver.FindElementById("login_btn_signin");

                fieldAccountName.Clear();
                fieldAccountName.SendKeys(_settings["steamUsername"]);
                fieldSteamPassword.Clear();
                fieldSteamPassword.SendKeys(_settings["steamPassword"]);

                if (_chromeDriver.ElementIsPresent(By.Id("input_captcha")))
                {
                    var fieldCaptcha = _chromeDriver.FindElementById("input_captcha");
                    fieldCaptcha.Clear();

                    Console.Write("Please enter captcha: ");
                    var captcha = Console.ReadLine();
                    fieldCaptcha.SendKeys(captcha);
                }

                buttonLogin.Click();
            }

            try
            {
                var wait = new WebDriverWait(_chromeDriver, TimeSpan.FromSeconds(5));
                wait.Until(condition =>
                {
                    try
                    {
                        var successButton = _chromeDriver.FindElement(By.Id("success_continue_btn"));
                        var avatar = _chromeDriver.FindElement(By.ClassName("avatar"));

                        return successButton.Displayed || avatar.Displayed;
                    }
                    catch (StaleElementReferenceException)
                    {
                        return false;
                    }
                    catch (NoSuchElementException)
                    {
                        return false;
                    }
                });
            }
            catch (WebDriverTimeoutException)
            {
                // what
            }

            if (_chromeDriver.ElementIsPresent(By.Id("success_continue_btn")) || _chromeDriver.ElementIsPresent(By.ClassName("avatar")))
            {
                _signedIn = true;
            }
            else if (_loginTries < 3)
            {
                _loginTries++;
                Login();
            }
        }

        private static void AddFromSearchResults()
        {
            if (!_settings.Keys.Contains("searchQueries"))
            {
                return;
            }

            foreach (var query in _settings["searchQueries"].Split(','))
            {
                var start = 0;
                do
                {
                    var url = "https://partner.steamgames.com/doc?q=" + query + "&start=" + start;
                    Console.WriteLine($"> Searching {url}");
                    _chromeDriver.Navigate().GoToUrl(url);
                    start += 20;
                }
                while (GetDocumentationLinks());
            }
        }

        private static bool GetDocumentationLinks()
        {
            var links = _chromeDriver.FindElementsByTagName("a");

            foreach (var link in links)
            {
                string href;

                try
                {
                    href = link.GetAttribute("href") ?? string.Empty;
                }
                catch (WebDriverException e)
                {
                    Console.WriteLine(e);
                    continue;
                }

                var match = _linkMatch.Match(href);

                if (!match.Success)
                {
                    continue;
                }

                href = match.Groups["href"].Value;

                // Fix for some broken links
                href = href.Replace("%3Fbeta%3D1", "");

                if (string.IsNullOrWhiteSpace(href) || DocumentationLinks.ContainsKey(href))
                {
                    continue;
                }

                DocumentationLinks.Add(href, false);

                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($" > Found a link: {href}");
                Console.ResetColor();
            }

            return _chromeDriver.ElementIsPresent(By.ClassName("docSearchResultLink"));
        }

        private static void FetchLinks()
        {
            IEnumerable<KeyValuePair<string, bool>> links;
            while ((links = DocumentationLinks.Where(l => l.Value == false).ToArray()).Any())
            {
                foreach (var link in links)
                {
                    SaveDocumentation(link.Key);

                    GetDocumentationLinks();
                }
            }
        }

        private static void SaveDocumentation(string link)
        {
            Console.WriteLine($"{Environment.NewLine}> Navigating to {link}");
            _chromeDriver.Navigate().GoToUrl("https://partner.steamgames.com/doc/" + link);

            var file = link;

            // Normal layout.
            var isAdminPage = _chromeDriver.ElementIsPresent(By.ClassName("documentation_bbcode"));

            IWebElement content = null;
            var html = string.Empty;

            if (isAdminPage)
            {
                content = _chromeDriver.FindElementByClassName("documentation_bbcode");
                html = content.GetAttribute("innerHTML");

                if (_chromeDriver.ElementIsPresent(By.ClassName("docPageTitle")))
                {
                    html = _chromeDriver.FindElementByClassName("docPageTitle").GetAttribute("innerHTML") + "\n" + html;
                }

                // Using stream because Document.FromString breaks encoding
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(html)))
                using (var doc = Document.FromStream(stream))
                {
                    doc.WrapAt = 0;
                    doc.OutputBodyOnly = AutoBool.Yes;
                    doc.IndentBlockElements = AutoBool.Yes;
                    doc.IndentSpaces = 4;
                    doc.ShowWarnings = false;
                    doc.Quiet = true;
                    doc.CleanAndRepair();
                    html = doc.Save();
                }

                if (html.Contains("Welcome to Steamworks!"))
                {
                    Console.WriteLine(" > Does not exist");
                    DocumentationLinks[link] = true;
                    return;
                }

                file += ".html";
            }
            else
            {
                // Unknown content. Save to a file.
                Console.WriteLine(" > Unknown content");
            }

            if (content != null)
            {
                var images = _chromeDriver.FindElements(By.CssSelector("img"));

                foreach (var img in images)
                {
                    var imgLink = img.GetAttribute("src");

                    if (imgLink.Contains("/steamcommunity/public/images/avatars/"))
                    {
                        continue;
                    }

                    var imgLink = img.GetAttribute("src");
                    var imgFile = Path.Combine(_imgsDirectory, Path.GetFileName(imgLink));

                    var index = imgFile.IndexOf("?", StringComparison.Ordinal);
                    if (index != -1)
                    {
                        imgFile = imgFile.Substring(0, index);
                    }

                    if (File.Exists(imgFile))
                    {
                        continue;
                    }

                    Console.WriteLine(" > Downloading image: {0}", imgFile);

                    if (!Directory.Exists(Path.GetDirectoryName(imgFile)))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(imgFile));
                    }

                    using (var client = new WebClient())
                    {
                        try
                        {
                            client.DownloadFile(imgLink, imgFile);
                        }
                        catch (Exception e)
                        {
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine(e.Message);
                            Console.ResetColor();
                        }
                    }
                }
            }
            else
            {
                Console.WriteLine(" > Missing content");
            }

            // Remove values which would leak user's auth tokens etc.

            const string matchPattern = @"name: ""(token|token_secure|auth|steamid|webcookie)"", value: ""[A-Za-z0-9\[\]_\-\:]+""";
            const string replacementPattern = @"name: ""$1"", value: ""hunter2""";
            html = Regex.Replace(html, matchPattern, replacementPattern);
            html = html.TrimEnd() + Environment.NewLine;

            file = file.Replace('\\', '/');

            if (Path.DirectorySeparatorChar != '/')
            {
                file = file.Replace('/', Path.DirectorySeparatorChar);
            }

            file = file.TrimStart(Path.DirectorySeparatorChar);
            file = Path.Combine(_docsDirectory, file);
            var folder = Path.GetDirectoryName(file);

            Console.WriteLine(file);

            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }

            File.WriteAllText(file, html, Encoding.UTF8);
            DocumentationLinks[link] = true;

            Console.WriteLine(" > Saved");
        }
    }
}
