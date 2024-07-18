using System; 
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using System.Linq;
using Serilog;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace CaptiveAutomation{
	public class Program{
		static async Task Main(string[] args){
		CultureInfo culture = new CultureInfo("en-US");
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .WriteTo.File($"logs/log_{DateTime.Now:yyyyMMdd_HHmmss}.txt", rollingInterval: RollingInterval.Infinite ,retainedFileCountLimit: null)
          .CreateLogger();
		await Starbucks(args[0]);
		}
        static async Task Starbucks(string mode)
        {
            var captiveUrl = await GetCaptivePortalUrlAsync();
            if(captiveUrl == null)
            {
                Log.Information("Captive portal url is null");
                captiveUrl = await GetCaptivePortalUrlWithCurlAsync();
            }
            Log.Information("Captive Url: " + captiveUrl);
            var options = new ChromeOptions();
            options.AddArguments("headless");
            options.AddArgument("--no-sandbox");
            // options.AddArgument("--user-data-dir=/home/jk/UserData");
            using( var driver = new ChromeDriver(options))
            {
                Log.Information("ChromeDriver Loaded");
                if(mode=="0")
                {
                    driver.Navigate().GoToUrl(captiveUrl); 
                    Thread.Sleep(2000);
                }
                else
                {
                    driver.Navigate().GoToUrl("https://google.com/");
                    Thread.Sleep(5000);
                    if(driver.Url != "https://www.google.com")
                    {
                        Log.Information($"Redirected to {driver.Url}");
                        File.WriteAllText($"html/redirect_{driver.Url.Replace("/","")}{DateTime.Now.ToString("HHmmss")}.html", driver.PageSource);
                    }
                }
                int i = 0;
                if(!driver.PageSource.Contains("error occurred"))
                {
                    File.WriteAllText($"html/error{DateTime.Now.ToString("HHmmss")}.html", driver.PageSource);
                    Log.Information("PAGE CONTAINS NO ERROR");
                }
                else
                {
                    try{
                        var english = driver.FindElement(By.XPath("//a[contains(translate(text(), 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz'), 'english')]"));
                        if(english != null)
                        {
                            try{
                                Log.Information("Clicking English Button");
                                Log.Information("English button html: " + english.GetAttribute("outerHTML"));
                                Thread.Sleep(1000);
                                english.Click();
                            }
                            catch(Exception ex)
                            {
                                Log.Information("Clicking via js");
                                IJavaScriptExecutor jsEx = (IJavaScriptExecutor)driver;
                                jsEx.ExecuteScript("arguments[0].click();", english);
                            }
                                Thread.Sleep(3000);
                        }
                        else
                        {
                            Log.Information("Found no english button");
                        }
                        Log.Information("Page Contains error before clicking english button");
                    }
                    catch(Exception ex)
                    {
                        
                    }
                }
                if(!driver.PageSource.Contains("error occurred"))
                {
                    File.WriteAllText($"html/welcome{DateTime.Now.ToString("HHmmss")}.html", driver.PageSource);
                    Log.Information("PAGE CONTAINS NO ERROR");
                }
                else
                {
                    Log.Information("Page Contains error after clicking english button");
                }
                while(await IsCaptivePortalAsync() && i < 10)
                {
                    await AttemptLogin(driver);
                    ++i;
                }
                driver.Quit();
            }
            Log.Information("Logged in");           
        }
		static async Task<String> GetCaptivePortalUrlAsync()
	    {
	        Log.Information("Getting Captive Portal Url");
	   		using (var client = new HttpClient())
	        {
	            client.DefaultRequestHeaders.AcceptLanguage.Clear();
	            client.DefaultRequestHeaders.AcceptLanguage.Add(new System.Net.Http.Headers.StringWithQualityHeaderValue("en-US"));
	            client.Timeout = TimeSpan.FromSeconds(5);
	            try
	            {
	                var response = await client.GetAsync("http://captive.apple.com/hotspot-detect.html");
	                if (response.IsSuccessStatusCode)
	                {
	                    string content = await response.Content.ReadAsStringAsync();
	                    if (!content.Contains("Success"))
	                    {
	                        return response.RequestMessage.RequestUri.ToString();
	                    }
	                }
	            }
	            catch (HttpRequestException)
	            {
	                // Handle exception (e.g., no internet connection)
	            }
	    }
	    return null;
	
	    }
		public static async Task<string> GetCaptivePortalUrlWithCurlAsync()
	    {
	        var startInfo = new ProcessStartInfo
	        {
	            FileName = "curl",
	            Arguments = "-v -k http://captive.apple.com/hotspot-detect.html",
	            RedirectStandardOutput = true,
	            RedirectStandardError = true,
	            UseShellExecute = false,
	            CreateNoWindow = true
	        };

	        using (var process = new Process { StartInfo = startInfo })
	        {
	            process.Start();
	            string output = await process.StandardError.ReadToEndAsync(); // curl writes verbose output to stderr
	            await process.WaitForExitAsync();

	            // Use regex to find the Location header
	            var match = Regex.Match(output, @"^< Location: (.*)$", RegexOptions.Multiline);
	            if (match.Success)
	            {
	                return match.Groups[1].Value.Trim();
	            }

	            Console.WriteLine("Full curl output:");
	            Console.WriteLine(output);

	            return null; // Return null if no Location header was found
	        }
	    }
		static async Task<bool> IsCaptivePortalAsync()
        {
        string url = "http://captive.apple.com/hotspot-detect.html";
            try
            {
                using (HttpClient client = new HttpClient())
                {
                    var response = await client.GetAsync(url);
                    if (response.StatusCode == System.Net.HttpStatusCode.OK)
                    {
                        string content = await response.Content.ReadAsStringAsync();
                        if (content.Contains("Success"))
                        {
                            return false; // No captive portal
                        }
                    }

                    return true; // Captive portal detected
                }
            }
            catch (Exception ex)
            {
                Log.Information($"Error checking captive portal: {ex.Message}");
                return false;
            }
        }
		static async Task<bool> AttemptLogin(IWebDriver driver)
        {
            var actions = new List<Func<IWebDriver, Task<bool>>>
            {
                TryClickToAcceptTerms,
                TryToEnterEmail
            }; 
            foreach (var action in actions)
            {
                if (await action(driver))
                {
                    return true;
                }
            }
            return false;
        }
		static async Task<bool> TryToEnterEmail(IWebDriver driver)
        {
            
            Log.Information("Trying to Enter Email");
            File.WriteAllText($"html/{driver.Url.Replace("/","")}{DateTime.Now.ToString("HHmmss")}.html", driver.PageSource);
            var textfields = driver.FindElements(By.XPath("//input[@type='text' or @type='email' or @type='password' and not(@type='hidden')]"));
            if(textfields.Count == 0)
            {
                Log.Information("No Text Fields Found");
                return false;
            }
            try
            {
                var email = driver.FindElement(By.XPath("//input[@type='email']"));
                Log.Information("Email Field: " + email.GetAttribute("outerHTML"));
                if (email != null)
                {
                    var tempmail = await RandomEmailGenerator.GenerateRandomEmailAsync();
                    Log.Information($"Temp mail: "+tempmail);
                    email.SendKeys(tempmail);
                    Thread.Sleep(1500);
                    var checkbox = await FindBestMatch(driver, "//input[@type='checkbox']", new[] { "accept", "acceptance", "accepted", "agree", "agreed", "agreement", "terms", "conditions", "terms and conditions", "consent", "approve", "approval", "acknowledge", "acknowledgment", "comply", "compliance" },new[] {"dont","don't","no","not","google","facebook","twitter" });
                    if (checkbox != null)
                    {
                        checkbox.Click();
                        Thread.Sleep(1000);
                    }
                    var signin= await FindBestMatch(driver, "//button | //input[@type='submit' or @type='button'] | //a[@href]", new[] { "agree", "accept", "continue", "connect", "confirm", "proceed", "next", "submit", "yes", "I agree", "I accept", "start", "join", "sign up", "register", "complete", "finish", "done", "okay", "allow", "authorize", "permit", "go", "ok", "sign", "login", "access", "authenticate", "enable" },new[] {"dont","don't","no","not","google","facebook","twitter" });
                    if (signin!= null)
                    {
                        signin.Click();
                        Thread.Sleep(2000);
                        return true;
                    }
                }                
            }
            catch (Exception e)
            {
                Log.Information($"Error in TryClickToAcceptTerms: {e.Message}");
            }
            Log.Information("Returning False");
            return false;
        }
		static async Task<bool> TryClickToAcceptTerms(IWebDriver driver)
        {
            Log.Information("Trying to accept terms");
            File.WriteAllText($"html/{driver.Url.Replace("/","")}{DateTime.Now.ToString("HHmmss")}.html", driver.PageSource);
            try
            {
                // Find and click the checkbox
                var checkbox = await FindBestMatch(driver, "//input[@type='checkbox']", new[] { "accept", "acceptance", "accepted", "agree", "agreed", "agreement", "terms", "conditions", "terms and conditions", "consent", "approve", "approval", "acknowledge", "acknowledgment", "comply", "compliance" },new[] {"dont","don't","no","not","google","facebook","twitter" });
                if (checkbox != null)
                {
                    checkbox.Click();
                    Thread.Sleep(1000);
                }
                var textfields = driver.FindElements(By.XPath("//input[@type='text' or @type='email' or @type='password' and not(@type='hidden')]"));
                if(textfields.Count !=0)
                {
                    Log.Information($"{textfields.Count} Text Fields Found");
                    return false;
                }
                Log.Information("Checking for agree button");
                // Find and click the agree button                    
                var agreeButton = await FindBestMatch(driver, "//button | //input[@type='submit' or @type='button'] | //a[@href]", new[] { "agree", "accept", "continue", "connect", "confirm", "proceed", "next", "submit", "yes", "I agree", "I accept", "start", "join", "sign up", "register", "complete", "finish", "done", "okay", "allow", "authorize", "permit", "go", "ok", "sign", "login", "access", "authenticate", "enable" },new[] {"dont","don't","no","not","google","facebook","twitter" });

                if (agreeButton != null)
                {
                    agreeButton.Click();
                    Thread.Sleep(2000);
                    return true;
                }
            }
            catch (Exception e)
            {
                Log.Information($"Error in TryClickToAcceptTerms: {e.Message}");
            }
            Log.Information("Returning False");
            return false;
        }
		static async Task<IWebElement> FindBestMatch(IWebDriver driver, string xpath, string[] keywords,string[] negative)
        {
            Log.Information("XPATH: " + xpath);
            var elements = driver.FindElements(By.XPath(xpath));
            Log.Information("Got Elements");
            Log.Information($"{elements.Count()} elements");
            foreach(var element in elements)
            {
                List<string> texts = new List<string>();            
                texts.Add(element.Text?.ToLower() ?? "");
                texts.Add(element.GetAttribute("value")?.ToLower() ?? "");
                texts.Add(element.GetAttribute("name")?.ToLower() ?? "");
                Log.Information("Element: " + element.GetAttribute("outerHTML"));
                foreach(string text in texts)
                {
                    if(keywords.Any(x=> text.Contains(x)) && !string.IsNullOrEmpty(text)&& !negative.Any(x=> text.Contains(x)))
                    {
                        Log.Information($"{keywords.Where(x=> x.Contains(text)).FirstOrDefault()} Matched with {text}");
                        return element;
                    }
                    else
                    {
                        if(negative.Any(x=> text.Contains(x)))
                        {
                            Log.Information($"Found a negative word {negative.Where(x=> text.Contains(x)).FirstOrDefault()} in {text}");
                        }
                    }
                }
            }
            return null;
        }
	}
	public class RandomEmailGenerator()
	{
	    private static readonly Random random = new Random();
	    private static readonly string[] domains = { "gmail.com", "yahoo.com", "hotmail.com", "outlook.com", "example.com" };

	    public static async Task<string> GenerateRandomEmailAsync()
	    {
	        string username = await GenerateRandomStringAsync(8);
	        string domain = await Task.Run(() => domains[random.Next(domains.Length)]);
	        return $"{username}@{domain}";
	    }

	    private static async Task<string> GenerateRandomStringAsync(int length)
	    {
	        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
	        StringBuilder sb = new StringBuilder(length);

	        await Task.Run(() =>
	        {
	            for (int i = 0; i < length; i++)
	            {
	                sb.Append(chars[random.Next(chars.Length)]);
	            }
	        });

	        return sb.ToString();
	    }
	}
}
