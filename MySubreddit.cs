#nullable disable
using Reddit;
using Reddit.Controllers;
using opennlp.tools.tokenize;
using opennlp.tools.sentdetect;
using opennlp.tools.namefind;
using System.Collections.Concurrent;
using System.Reactive.Subjects;
using System.Reactive.Linq;
using System.Reactive.Concurrency;

namespace Project3
{
    internal class MySubreddit : IObserver<string>, IObservable<string>
    {
        // reactive programming
        private ISubject<string> resultsSubject;
        private readonly IScheduler scheduler;

        // locks
        private object locker;
        private static object consolelocker;

        // tasks
        public ConcurrentBag<Task> Tasks;

        // subreddit
        public string Name { get; set; }
        public RedditClient Reddit { get; set; }

        // comments
        public int NumComments { get; set; }
        public ConcurrentBag<string> CommentText { get; set; }
        public ConcurrentBag<string> CommentIds { get; set; }

        public MySubreddit(RedditClient redit, string name)
        {
            Reddit = redit;
            Name = name;
            NumComments = 0;
            locker = new object();
            CommentIds = new ConcurrentBag<string>();
            CommentText = new ConcurrentBag<string>();
            Tasks = new ConcurrentBag<Task>();
            resultsSubject = new Subject<string>();
            consolelocker = new object();
            scheduler = new EventLoopScheduler();
        }

        private void IterateComments(IList<Comment> comments, Post post, int depth = 0)
        {
            foreach (Comment comment in comments)
            {
                AddComment(comment, depth);
                IterateComments(comment.Replies, post, (depth + 1));
                IterateComments(GetMoreChildren(comment, post), post, depth);
            }
        }

        private IList<Comment> GetMoreChildren(Comment comment, Post post)
        {
            List<Comment> res = new List<Comment>();
            if (comment.More == null)
            {
                return res;
            }

            foreach (Reddit.Things.More more in comment.More)
            {
                foreach (string id in more.Children)
                {
                    if (!CommentIds.Contains(id))
                    {
                        res.Add(post.Comment("t1_" + id).About());
                    }
                }
            }

            return res;
        }

        private void AddComment(Comment comment, int depth = 0)
        {
            if (comment == null || string.IsNullOrWhiteSpace(comment.Author))
            {
                return;
            }

            lock (locker)
            {
                NumComments++;
            }

            if (!CommentIds.Contains(comment.Id))
            {
                CommentIds.Add(comment.Id);
            }

            CommentText.Add(comment.Body);
        }

        private void AnalyzeComments()
        {
            // Path to the models
            string sentenceModelPath = @"..\..\..\Models\opennlp-en-ud-ewt-sentence-1.0-1.9.3.bin";
            string tokenModelPath = @"..\..\..\Models\opennlp-en-ud-ewt-tokens-1.0-1.9.3.bin";
            string nameModelPath = @"..\..\..\Models\en-ner-location.bin";

            // Loading the models
            var sentenceModelStream = new java.io.FileInputStream(sentenceModelPath);
            var sentenceModel = new SentenceModel(sentenceModelStream);
            var sentenceDetector = new SentenceDetectorME(sentenceModel);

            var tokenModelStream = new java.io.FileInputStream(tokenModelPath);
            var tokenModel = new TokenizerModel(tokenModelStream);
            var tokenizer = new TokenizerME(tokenModel);

            var nameModelStream = new java.io.FileInputStream(nameModelPath);
            var nameModel = new TokenNameFinderModel(nameModelStream);
            var nameFinder = new NameFinderME(nameModel);


            // Perform tokenization
            foreach (string comm in CommentText)
            {
                string[] sentences = sentenceDetector.sentDetect(comm);
                foreach (string sentence in sentences)
                {
                    string[] tokens = tokenizer.tokenize(sentence);
                    try
                    {
                        var nameSpans = nameFinder.find(tokens);

                        foreach (var span in nameSpans)
                        {
                            var entity = String.Join(" ", tokens, span.getStart(), span.length());
                            var entityType = span.getType();
                            Console.WriteLine($"{entity} - {entityType}");
                            resultsSubject.OnNext($"{entity} - {entityType}");
                        }

                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"An error occurred during NER analysis: {ex.Message}");
                    }

                }
            }

            resultsSubject.OnCompleted();

            // Close the model streams
            tokenModelStream.close();
            sentenceModelStream.close();
        }

        private async Task<bool> RunTask(string postId)
        {
            try
            {
                await Task.Delay(new Random().Next(10, 50));

                Post post = Reddit.Subreddit(Name).Post(postId).About();

                lock (consolelocker)
                {
                    Console.WriteLine(post.Title);
                    Console.WriteLine("There are " + post.Listing.NumComments + " comments");
                }

                IterateComments(post.Comments.GetNew(limit: 500), post);
                return true;
            }
            catch (Exception ex)
            {
                lock (consolelocker)
                {
                    Console.ForegroundColor = ConsoleColor.Magenta;
                    Console.WriteLine(ex.Message);
                    Console.ForegroundColor = ConsoleColor.White;
                }
                return false;
            }   
        }
        public async void OnNext(string postId)
        {
            int currentTry = 0;
            int maxTries = 3;
            bool success = false;

            while (!success && currentTry < maxTries)
            {             
                var task = RunTask(postId);
                Tasks.Add(task);
                success = await task;

                if (currentTry > 0)
                    lock (consolelocker)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine("From Attempt " + currentTry);
                        Console.ForegroundColor = ConsoleColor.White;
                    }

                if (!success) 
                {
                    currentTry++;

                    lock (consolelocker)
                    {
                        Console.ForegroundColor = ConsoleColor.Magenta;
                        Console.WriteLine("Attempt number " + currentTry);
                        Console.ForegroundColor = ConsoleColor.White;
                    }
                }
            }
        }

        public void OnCompleted()
        {
            Task.WaitAll(Tasks.ToArray());

            lock(consolelocker) 
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"\nSubreddit {Name}\nTotal comments to analyze: {NumComments}\n");
                Console.ForegroundColor = ConsoleColor.White;
            }

            AnalyzeComments();
        }

        public void OnError(Exception error)
        {
            resultsSubject.OnError(error);
        }

        public IDisposable Subscribe(IObserver<string> observer)
        {
            //return resultsSubject.Subscribe(observer);
            return resultsSubject.ObserveOn(scheduler).Subscribe(observer);
        }
    }
}
