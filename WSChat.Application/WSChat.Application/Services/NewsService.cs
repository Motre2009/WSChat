using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace WSChat.Application.Services;

public class NewsService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;

    public NewsService(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "WSChat/1.0");
    }

    public async Task<List<NewsArticle>> GetTopHeadlinesAsync(string category = "general", string country = "us", int pageSize = 5)
    {
        try
        {
            var apiKey = _configuration["NewsApi:Key"];

            if (string.IsNullOrEmpty(apiKey) || apiKey == "demo_key_for_testing" || apiKey == "your_newsapi_key_here")
            {
                return GetMockNews(category);
            }

            var url = $"https://newsapi.org/v2/top-headlines?category={category}&country={country}&pageSize={pageSize}&apiKey={apiKey}";

            var response = await _httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                return GetMockNews(category);
            }

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<NewsApiResponse>(json);

            if (result?.Articles == null || result.Articles.Count == 0)
            {
                return GetMockNews(category);
            }

            return result.Articles
                .Where(a => !string.IsNullOrEmpty(a.Title))
                .Take(pageSize)
                .ToList();
        }
        catch (Exception)
        {
            return GetMockNews(category);
        }
    }

    public async Task<string> GetNewsAsync(string category = "general")
    {
        var articles = await GetTopHeadlinesAsync(category);

        if (articles == null || articles.Count == 0)
        {
            return $"No news found in {category} category.";
        }

        var summary = $"📰 Top {articles.Count} {category} news:\n\n";

        for (int i = 0; i < articles.Count; i++)
        {
            var article = articles[i];
            summary += $"{i + 1}. {article.Title}\n";
            if (!string.IsNullOrEmpty(article.Description))
            {
                summary += $"   {article.Description.Truncate(100)}\n";
            }
            if (!string.IsNullOrEmpty(article.Source?.Name))
            {
                summary += $"   Source: {article.Source.Name}\n";
            }
            summary += "\n";
        }

        return summary.Trim();
    }

    private List<NewsArticle> GetMockNews(string category)
    {
        var mockNews = new List<NewsArticle>();
        var now = DateTime.Now;

        var categories = new Dictionary<string, List<(string title, string desc)>>
        {
            ["technology"] = new()
            {
                ("AI Breakthrough: New Model Beats Human Performance", "Researchers developed an AI that can solve complex problems."),
                ("Quantum Computing Milestone Achieved", "Scientists reach new record in quantum computing stability."),
                ("New Programming Language Released", "Simpler syntax and better performance promised."),
                ("Cybersecurity Threat Alert", "New vulnerability affects major operating systems."),
                ("Tech Giant Announces New Product Line", "Innovative devices set to launch next quarter.")
            },
            ["business"] = new()
            {
                ("Stock Market Hits Record High", "Investors optimistic about economic recovery."),
                ("Major Merger Announced in Tech Sector", "Deal valued at billions of dollars."),
                ("New Startup Reaches Unicorn Status", "Company valuation exceeds $1 billion."),
                ("Central Bank Announces Interest Rate Decision", "Economists analyze potential impacts."),
                ("Global Trade Agreement Signed", "New pact expected to boost international commerce.")
            },
            ["sports"] = new()
            {
                ("Championship Final This Weekend", "Top teams compete for the title."),
                ("Athlete Breaks World Record", "New milestone set in track and field."),
                ("Team Signs Star Player", "Major transfer shakes up the league."),
                ("Olympic Preparations Underway", "Host city gears up for games."),
                ("Surprise Victory in Tournament", "Underdog team wins against all odds.")
            },
            ["entertainment"] = new()
            {
                ("Award Show Winners Announced", "Stars gather for prestigious ceremony."),
                ("New Blockbuster Film Release", "Movie breaks opening weekend records."),
                ("Celebrity Wedding Announcement", "Famous couple ties the knot."),
                ("Music Album Debuts at Number One", "Artist achieves chart success."),
                ("TV Series Renewed for New Season", "Fans celebrate the announcement.")
            }
        };

        if (categories.TryGetValue(category.ToLower(), out var newsList))
        {
            for (int i = 0; i < Math.Min(5, newsList.Count); i++)
            {
                mockNews.Add(new NewsArticle
                {
                    Title = newsList[i].title,
                    Description = newsList[i].desc,
                    Source = new NewsSource { Name = "Mock News" },
                    PublishedAt = now.AddHours(-i)
                });
            }
        }
        else
        {
            for (int i = 1; i <= 5; i++)
            {
                mockNews.Add(new NewsArticle
                {
                    Title = $"Important News Update #{i}",
                    Description = $"Latest developments in current events. Category: {category}",
                    Source = new NewsSource { Name = "WSChat News" },
                    PublishedAt = now.AddHours(-i)
                });
            }
        }

        return mockNews;
    }
}

public class NewsApiResponse
{
    public string Status { get; set; } = string.Empty;
    public int TotalResults { get; set; }
    public List<NewsArticle> Articles { get; set; } = new();
}

public class NewsArticle
{
    public NewsSource? Source { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public DateTime PublishedAt { get; set; }
}

public class NewsSource
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}
public static class StringExtensions
{
    public static string Truncate(this string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= maxLength ? value : value.Substring(0, maxLength) + "...";
    }
}