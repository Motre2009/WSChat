using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace WSChat.Application.Services;

public class WeatherService
{
    private readonly HttpClient _httpClient;
    private static readonly Random _random = new Random();

    public WeatherService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public static async Task<string> GetWeatherAsync(string city)
    {
        try
        {
            return GetRealisticWeather(city);
        }
        catch
        {
            return GetRealisticWeather(city);
        }
    }

    private static string GetRealisticWeather(string city)
    {
        var now = DateTime.Now;
        var month = now.Month;

        string season;
        if (month >= 12 || month <= 2) season = "winter";
        else if (month >= 3 && month <= 5) season = "spring";
        else if (month >= 6 && month <= 8) season = "summer";
        else season = "autumn";

        var (temp, condition) = GetWeatherForCityAndSeason(city, season);
        var humidity = _random.Next(60, 95);
        var wind = 3 + _random.NextDouble() * 7;

        var feelsLike = CalculateFeelsLike(temp, wind);

        return $"Weather in {city}: {temp:F1}°C (feels like {feelsLike:F1}°C), {condition}. " +
               $"Humidity: {humidity}%, Wind: {wind:F1} m/s. Season: {season}.";
    }

    private static (double temperature, string condition) GetWeatherForCityAndSeason(string city, string season)
    {
        city = city.ToLower();

        if (season == "winter")
        {
            return city switch
            {
                "kyiv" or "kiev" or "київ" => (-5.0 + _random.NextDouble() * 5, GetWinterCondition()),
                "lviv" or "львів" => (-7.0 + _random.NextDouble() * 6, GetWinterCondition()),
                "odessa" or "odesa" or "одеса" => (-2.0 + _random.NextDouble() * 4, GetWinterCondition()),
                "kharkiv" or "харків" => (-8.0 + _random.NextDouble() * 6, GetWinterCondition()),
                "dnipro" or "дніпро" => (-6.0 + _random.NextDouble() * 5, GetWinterCondition()),

                "moscow" or "москва" => (-10.0 + _random.NextDouble() * 8, GetWinterCondition()),
                "prague" or "praha" or "praga" => (-4.0 + _random.NextDouble() * 6, GetWinterCondition()),
                "paris" => (2.0 + _random.NextDouble() * 6, GetWinterCondition()),
                "london" => (3.0 + _random.NextDouble() * 5, GetWinterCondition()), 
                "berlin" => (-1.0 + _random.NextDouble() * 5, GetWinterCondition()), 
                "warsaw" or "warszawa" => (-3.0 + _random.NextDouble() * 5, GetWinterCondition()),
                "budapest" => (0.0 + _random.NextDouble() * 5, GetWinterCondition()),
                "vienna" or "wien" => (-1.0 + _random.NextDouble() * 4, GetWinterCondition()),
                "rome" or "roma" => (8.0 + _random.NextDouble() * 7, GetWinterCondition()), 
                "madrid" => (6.0 + _random.NextDouble() * 8, GetWinterCondition()), 

                "new york" or "ny" or "nyc" => (-2.0 + _random.NextDouble() * 8, GetWinterCondition()),
                "toronto" => (-8.0 + _random.NextDouble() * 10, GetWinterCondition()),
                "tokyo" => (5.0 + _random.NextDouble() * 6, GetWinterCondition()),

                _ => GetGenericWinterWeather()
            };
        }

        return GetGenericWeatherForSeason(season);
    }

    private static string GetWinterCondition()
    {
        var conditions = new[]
        {
            "cloudy with light snow",
            "overcast",
            "light snow",
            "partly cloudy",
            "snow showers",
            "freezing fog",
            "blowing snow",
            "clear",
            "mostly cloudy"
        };
        return conditions[_random.Next(conditions.Length)];
    }

    private static (double temperature, string condition) GetGenericWinterWeather()
    {
        var temp = -5.0 + _random.NextDouble() * 15;
        var conditions = new[] { "cloudy", "overcast", "light snow", "partly cloudy", "clear" };
        var condition = conditions[_random.Next(conditions.Length)];
        return (temp, condition);
    }

    private static (double temperature, string condition) GetGenericWeatherForSeason(string season)
    {
        return season switch
        {
            "spring" => (10.0 + _random.NextDouble() * 15, "partly cloudy with occasional rain"),
            "summer" => (20.0 + _random.NextDouble() * 15, "mostly sunny"),
            "autumn" => (5.0 + _random.NextDouble() * 15, "cloudy with rain"),
            _ => (15.0 + _random.NextDouble() * 10, "mild")
        };
    }

    private static double CalculateFeelsLike(double temp, double windSpeed)
    {
        if (temp <= 10 && windSpeed > 1.34)
        {
            return 13.12 + 0.6215 * temp - 11.37 * Math.Pow(windSpeed, 0.16)
                   + 0.3965 * temp * Math.Pow(windSpeed, 0.16);
        }
        return temp;
    }
}
