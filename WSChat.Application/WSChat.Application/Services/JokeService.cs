using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace WSChat.Application.Services;

public class JokeService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<JokeService> _logger;

    public JokeService(HttpClient httpClient, IConfiguration configuration, ILogger<JokeService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;

        _httpClient.DefaultRequestHeaders.Add("User-Agent", "WSChat/1.0");
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
    }

    public async Task<string> GetRandomJokeAsync()
    {
        try
        {
            var categories = _configuration["JokeApi:Categories"] ?? "Any";
            var url = $"https://v2.jokeapi.dev/joke/{categories}?safe-mode&type=twopart";

            _logger.LogInformation($"Fetching joke from: {url}");

            var response = await _httpClient.GetAsync(url);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var jokeData = JsonSerializer.Deserialize<JokeApiResponse>(json);

                if (jokeData != null && jokeData.Error != true)
                {
                    if (!string.IsNullOrEmpty(jokeData.Setup) && !string.IsNullOrEmpty(jokeData.Delivery))
                    {
                        return $"😄 {jokeData.Setup}\n\n🤣 {jokeData.Delivery}";
                    }
                    else if (!string.IsNullOrEmpty(jokeData.Joke))
                    {
                        return $"😄 {jokeData.Joke}";
                    }
                }
            }

            _logger.LogWarning($"Joke API returned {response.StatusCode}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching joke from API");
        }

        return GetLocalJoke();
    }

    public async Task<string> GetProgrammingJokeAsync()
    {
        try
        {
            var url = "https://v2.jokeapi.dev/joke/Programming?safe-mode&type=single";
            var response = await _httpClient.GetAsync(url);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var jokeData = JsonSerializer.Deserialize<JokeApiResponse>(json);

                if (jokeData != null && jokeData.Error != true && !string.IsNullOrEmpty(jokeData.Joke))
                {
                    return $"💻 Programming Joke:\n{jokeData.Joke}";
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching programming joke");
        }

        return GetLocalProgrammingJoke();
    }

    public async Task<string> GetQuoteAsync()
    {
        try
        {
            var url = "https://api.quotable.io/random";
            var response = await _httpClient.GetAsync(url);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var quoteData = JsonSerializer.Deserialize<QuoteApiResponse>(json);

                if (quoteData != null)
                {
                    return $"💭 Quote:\n\"{quoteData.Content}\"\n\n— {quoteData.Author}";
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching quote");
        }

        return GetLocalQuote();
    }

    private string GetLocalJoke()
    {
        var jokes = new[]
        {
            "Why don't scientists trust atoms?\nBecause they make up everything!",
            "Why did the scarecrow win an award?\nHe was outstanding in his field!",
            "What do you call a fake noodle?\nAn impasta!",
            "How does a penguin build its house?\nIgloos it together!",
            "Why don't eggs tell jokes?\nThey'd crack each other up!"
        };

        var random = new Random();
        return $"😄 {jokes[random.Next(jokes.Length)]}";
    }

    private string GetLocalProgrammingJoke()
    {
        var jokes = new[]
        {
            "Why do programmers prefer dark mode?\nBecause light attracts bugs!",
            "How many programmers does it take to change a light bulb?\nNone, that's a hardware problem!",
            "Why did the programmer quit his job?\nBecause he didn't get arrays!",
            "What's the object-oriented way to become wealthy?\nInheritance!",
            "Why do Java developers wear glasses?\nBecause they don't C#!"
        };

        var random = new Random();
        return $"💻 {jokes[random.Next(jokes.Length)]}";
    }

    private string GetLocalQuote()
    {
        var quotes = new[]
        {
            "The only way to do great work is to love what you do. — Steve Jobs",
            "Innovation distinguishes between a leader and a follower. — Steve Jobs",
            "The future belongs to those who believe in the beauty of their dreams. — Eleanor Roosevelt",
            "Stay hungry, stay foolish. — Steve Jobs",
            "Code is like humor. When you have to explain it, it's bad. — Cory House"
        };

        var random = new Random();
        return $"💭 {quotes[random.Next(quotes.Length)]}";
    }
}

public class JokeApiResponse
{
    public bool Error { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Setup { get; set; } = string.Empty;
    public string Delivery { get; set; } = string.Empty;
    public string Joke { get; set; } = string.Empty;
    public Flags Flags { get; set; } = new();
    public int Id { get; set; }
    public bool Safe { get; set; }
    public string Lang { get; set; } = string.Empty;
}

public class Flags
{
    public bool Nsfw { get; set; }
    public bool Religious { get; set; }
    public bool Political { get; set; }
    public bool Racist { get; set; }
    public bool Sexist { get; set; }
    public bool Explicit { get; set; }
}

public class QuoteApiResponse
{
    public string Id { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string[] Tags { get; set; } = Array.Empty<string>();
    public int Length { get; set; }
}