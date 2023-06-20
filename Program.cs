using Newtonsoft.Json;
using Project3;
using Reddit;
using System.Net;
using System.Reactive.Linq;


string listeningPort = "http://localhost:5000/";

// for authorization and access token
string baseEndpoint = "https://www.reddit.com/api/v1/";
string authorizationEndpoint = baseEndpoint + "authorize";
string tokenEndpoint = baseEndpoint + "access_token";
string scope = "read identity submit";
string userAgent = "This is a system programming app used for fetching all comments for subreddits";
string redirectUri = listeningPort;

// for access token
string clientId = ""; // your client id;
string clientSecret = ""; // your client secret;
RedditToken token = new RedditToken();
DateTime? expirationTime = null;


// shared resources
int numberOfRequest = 0;
object consoleLocker = new object();
SemaphoreSlim semaphoreSlim = new SemaphoreSlim(1, 1);



StartServer();


#region server
void StartServer()
{
    Thread serverThread = new Thread(() =>
    {
        HttpListener listener = new HttpListener();
        listener.Prefixes.Add(listeningPort);
        listener.Start();
        Console.WriteLine($"Server is listening on {listener.Prefixes.Last()}");

        while (true)
        {
            var context = listener.GetContext();

            Task task = Task.Run(async () =>
            {
                var request = context.Request;
                var response = context.Response;
                Response res = new Response();
                int myRequestNumber = 0;

                lock (consoleLocker)
                {
                    myRequestNumber = ++numberOfRequest;
                    LogRequest(request, myRequestNumber);
                }

                if (request.HttpMethod != "GET")
                {
                    res.text = "This is not a valid request! Only GET methods are allowed";
                    res.byteBuffer = System.Text.Encoding.UTF8.GetBytes(res.text);
                }
                else
                {
                    res = new Response();
                    List<string> subrredits = ParseQueryString(request);

                    if (subrredits != null)
                    {
                        if (expirationTime == null || DateTime.Now.Subtract(expirationTime.Value).Seconds >= 77760)
                        {
                            await semaphoreSlim.WaitAsync();
                            if (expirationTime == null || DateTime.Now.Subtract(expirationTime.Value).Seconds >= 77760)
                                await GetAccessTokenAsync();
                            semaphoreSlim.Release();
                        }

                        // creating subreddits

                        MySubreddit[] subreds = new MySubreddit[subrredits.Count];
                        for (int i = 0; i < subrredits.Count; i++)
                        {
                            var reddit = new RedditClient(appId: clientId, refreshToken: token.refresh_token, accessToken: token.access_token);
                            subreds[i] = new MySubreddit(reddit, subrredits[i]);
                        }

                        // fetching posts for subreddits

                        var mergedObservable = Observable.Merge(subreds);
                        var subscription = mergedObservable.Subscribe(res);
                        IDisposable[] subscriptions = new IDisposable[subreds.Length];

                        int j = 0;
                        foreach (var sub in subreds)
                        {
                            Console.WriteLine("Fetching posts for subreddit: " + sub.Name);
                            var stream = new PostStream(userAgent, token.access_token);
                            subscriptions[j++] = stream.Subscribe(sub);
                            await stream.GetSubredditPosts(sub.Name);
                        }

                        while (res.IsCompleted == false) { }

                        subscription.Dispose();
                        foreach (var sub in subscriptions)
                        {
                            sub.Dispose();
                        }
                    }
                    else
                    {
                        res.text = "This is not a valid request! subreddit parameter is missing";
                        res.byteBuffer = System.Text.Encoding.UTF8.GetBytes(res.text);
                    }
                }

                await SendResponseAsync(response, res);

                lock (consoleLocker)
                {
                    LogResponse(response, myRequestNumber);
                }
            });
        }

        listener.Close();
    });

    serverThread.Start();
    serverThread.Join();
}
#endregion


#region functions

async Task GetAccessTokenAsync()
{
    var client = new HttpClient();

    var tokenRequestBody = new Dictionary<string, string>
    {
        { "grant_type", "client_credentials" },
        { "scope", scope }
    };

    client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
        "Basic", Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{clientId}:{clientSecret}")));
    client.DefaultRequestHeaders.Add("User-Agent", $"{userAgent}");

    var tokenResponse = await client.PostAsync(tokenEndpoint, new FormUrlEncodedContent(tokenRequestBody));
    var responseContent = await tokenResponse.Content.ReadAsStringAsync();

    client.Dispose();

    token = JsonConvert.DeserializeObject<RedditToken>(responseContent);
    expirationTime = DateTime.Now;

    lock (consoleLocker)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("Reddit token refreshed");
        Console.ResetColor();
    }
}

void LogRequest(HttpListenerRequest request, int requestNumber)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"Logging request {requestNumber}");
    Console.ResetColor();
    Console.WriteLine(request.HttpMethod);
    Console.WriteLine(request.ProtocolVersion);
    Console.WriteLine(request.Url);
    Console.WriteLine(request.RawUrl);
    Console.WriteLine(request.Headers);
}

void LogResponse(HttpListenerResponse response, int requestNumber)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"Logging response for request {requestNumber}");
    Console.ResetColor();
    Console.WriteLine(response.StatusCode);
    Console.WriteLine(response.StatusDescription);
    Console.WriteLine(response.ProtocolVersion);
    Console.WriteLine(response.Headers);
}

List<string> ParseQueryString(HttpListenerRequest request)
{
    List<string> subreddits = new List<string>();
    bool subredditFound = false;

    for (int i = 0; i < request.QueryString.Count; i++)
    {
        var key = request.QueryString.GetKey(i);
        var values = request.QueryString.GetValues(key);

        if (key == "subreddit")
        {
            if (values.Length > 0)
            {
                foreach (var val in values)
                {
                    subreddits.Add(Uri.EscapeDataString(val));
                }
            }
            subredditFound = true;
        }
    }
    if (subredditFound)
        return subreddits;
    else
        return null;
}

async Task SendResponseAsync(HttpListenerResponse response, Response res)
{
    response.ContentLength64 = res.byteBuffer.Length;
    var output = response.OutputStream;
    await output.WriteAsync(res.byteBuffer, 0, res.byteBuffer.Length);
    output.Close();
}
#endregion


#region classes
class Response : IObserver<string>
{
    public string text = "";
    public byte[] byteBuffer;
    public bool IsCompleted { get; private set; }

    public void OnNext(string value)
    {
        text = text + value + "\n";
    }
    public void OnCompleted()
    {
        if (this.text.Length == 0)
        {
            this.text = "No locations found!";
        }
        this.byteBuffer = System.Text.Encoding.UTF8.GetBytes(this.text);
        IsCompleted = true;
    }
    public void OnError(Exception error)
    {
        text = text + error.Message + "\n";
        this.byteBuffer = System.Text.Encoding.UTF8.GetBytes(this.text);
        IsCompleted = true;
    }
}
class RedditToken
{
    public string access_token { get; set; }
    public string token_type { get; set; }
    public int expires_in { get; set; }
    public string refresh_token { get; set; }
    public string scope { get; set; }
}
#endregion