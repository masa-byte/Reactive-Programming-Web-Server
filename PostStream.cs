using Newtonsoft.Json.Linq;
using System.Net.Http.Headers;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace Project3
{
    internal class PostStream : IObservable<string>
    {
        // reactive programming
        private readonly IScheduler scheduler;
        private ISubject<string> postSubject;

        public string UserAgent { get; set; }
        public string Token { get; set; }

        public PostStream(string userAgent, string token)
        {
            postSubject = new Subject<string>();
            scheduler = new EventLoopScheduler();
            UserAgent = userAgent;
            Token = token;
        }
        public async Task GetSubredditPosts(string subredditName)
        {
            string responseBody;
            List<string> posts = new List<string>();
            string url = $"https://oauth.reddit.com/r/{subredditName}/new.json";

            HttpClient client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            client.DefaultRequestHeaders.Add("User-Agent", $"{UserAgent}");

            try
            {
                var response = await client.GetAsync(url);
                responseBody = await response.Content.ReadAsStringAsync();

                client.Dispose();

                // extracting post id's

                JObject responseJson = JObject.Parse(responseBody);
                JArray children = (JArray)responseJson["data"]!["children"]!;

                foreach (JToken child in children)
                {
                    JObject postData = (JObject)child["data"]!;
                    string name = (string)postData["name"]!;

                    if (name != "")
                    {
                        postSubject.OnNext(name!);
                    }
                }
                postSubject.OnCompleted();
            }
            catch (Exception ex)
            {
                postSubject.OnError(ex);
            }
        }

        public IDisposable Subscribe(IObserver<string> observer)
        {
            return postSubject.ObserveOn(scheduler).Subscribe(observer);
            //return postSubject.Subscribe(observer);
        }
    }
}
