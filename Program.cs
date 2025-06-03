using System.Net;
using System.Text;
using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.InputFiles;
using Telegram.Bot.Types.ReplyMarkups;
using System.Net.Http.Json;
using DotNetEnv;

try
{
    Env.TraversePath().Load();
}
catch
{
    Console.WriteLine("⚠️ Не вдалося знайти або завантажити .env файл.");
}

var token = Environment.GetEnvironmentVariable("BOT_TOKEN");

if (string.IsNullOrEmpty(token))
{
    Console.WriteLine("❌ BOT_TOKEN не знайдено у змінних середовища.");
    return;
}

var botClient = new TelegramBotClient(token);

await botClient.DeleteWebhookAsync();


using var cts = new CancellationTokenSource();

var receiverOptions = new ReceiverOptions
{
    AllowedUpdates = Array.Empty<UpdateType>()
};

Dictionary<long, string> userStates = new();
Dictionary<long, bool> waitingForTime = new();

bool waitingForDeleteId = false;
Dictionary<long, List<DateTime>> quoteRequests = new();
Dictionary<long, List<DateTime>> imageRequests = new();
Dictionary<long, QuoteResponse?> lastQuotes = new();
Dictionary<long, HashSet<int>> userSeenQuotes = new();
const int MAX_QUOTES_PER_USER = 50;

QuoteResponse? lastQuote = null;

const int REQUEST_LIMIT = 5;
const int LIMIT_SECONDS = 40;


bool IsRateLimited(Dictionary<long, List<DateTime>> requestMap, long chatId)
{
    var now = DateTime.UtcNow;

    if (!requestMap.ContainsKey(chatId))
        requestMap[chatId] = new List<DateTime>();

    requestMap[chatId].RemoveAll(t => (now - t).TotalSeconds > LIMIT_SECONDS);

    if (requestMap[chatId].Count >= REQUEST_LIMIT)
        return true;

    requestMap[chatId].Add(now);
    return false;
}


botClient.StartReceiving(
    HandleUpdateAsync,
    HandleErrorAsync,
    receiverOptions,
    cancellationToken: cts.Token
);

var me = await botClient.GetMeAsync();
Console.WriteLine($"✅ Бот {me.Username} запущено");

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

await Task.Delay(-1, cts.Token);

async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken token)
{

    if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery != null)
    {
        var callbackData = update.CallbackQuery.Data;
        var callbackChatId = update.CallbackQuery.Message?.Chat.Id ?? 0;
        var userId = update.CallbackQuery.From.Id;

        if (!string.IsNullOrEmpty(callbackData) &&
            (callbackData.StartsWith("like:") || callbackData.StartsWith("dislike:")))
        {
            var parts = callbackData.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[1], out int quoteId))
            {
                await bot.AnswerCallbackQueryAsync(update.CallbackQuery.Id, "❌ Невірний формат даних.");
                return;
            }

            var reactionType = parts[0];
            var apiUrl = "https://motivation-quotes-api-production.up.railway.app/quotes/react";

            var payload = new
            {
                QuoteId = quoteId,
                UserId = userId,
                ReactionType = reactionType
            };

            try
            {
                using var http = new HttpClient();
                var apiResponse = await http.PostAsJsonAsync(apiUrl, payload);

                if (!apiResponse.IsSuccessStatusCode)
                {
                    await bot.AnswerCallbackQueryAsync(update.CallbackQuery.Id, "⚠️ Помилка під час обробки реакції.");
                    return;
                }

                var result = await apiResponse.Content.ReadFromJsonAsync<ReactionResult>();
                var message = update.CallbackQuery.Message;

                if (message?.Text == null || message.ReplyMarkup == null)
                {
                    await bot.AnswerCallbackQueryAsync(update.CallbackQuery.Id);
                    return;
                }

                var lines = message.Text.Split('\n');
                if (lines.Length < 2)
                {
                    await bot.AnswerCallbackQueryAsync(update.CallbackQuery.Id);
                    return;
                }

                var updatedText = $"{lines[0]}\n{lines[1]}\n\n👍 {result?.Likes ?? 0}   👎 {result?.Dislikes ?? 0}";

                try
                {
                    await bot.EditMessageTextAsync(
                        chatId: callbackChatId,
                        messageId: message.MessageId,
                        text: updatedText,
                        replyMarkup: message.ReplyMarkup
                    );
                }
                catch (Telegram.Bot.Exceptions.ApiRequestException ex) when (ex.Message.Contains("message is not modified"))
                {
                    Console.WriteLine("⚠️ Повідомлення вже актуальне, редагування не потрібне.");
                }

                await bot.AnswerCallbackQueryAsync(update.CallbackQuery.Id);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Помилка реакції: {ex.Message}");
                await bot.AnswerCallbackQueryAsync(update.CallbackQuery.Id, "❌ Сталася помилка.");
            }
        }

        return;
    }



    if (update.Type != UpdateType.Message || update.Message?.Text is null)
        return;

    var chatId = update.Message.Chat.Id;
    var text = update.Message.Text.Trim();

    if (text == "/start")
    {
        string welcome = """
        Привіт! Я — твій особистий мотиватор у важкі моменти життя 💪✨
        Я тут, щоб надихати тебе щодня, підтримувати, коли не вистачає сил, і дарувати слова, які змусять повірити в себе.

        Ось що я вмію:
        🔹 /random — надішлю тобі випадкову цитату.
        🔹 /favorites — покажу всі твої збережені улюблені цитати.
        🔹 /save — збережу останню надіслану цитату в улюблені.
        🔹 /delete — видалю цитату з улюблених за її ID.
        🔹 /history — покажу 5 останніх отриманих цитат.
        🔹 /clear_history — повністю очищу історію переглядів.
        🔹 /image — покажу картинку з цитатою.
        
        🧠 Усе, що ти зберігаєш, не зникає — я пам’ятаю твої улюблені думки й навіть історію запитів.
        Пиши мені, коли сумно, коли радісно або просто хочеш мудре слово 🌟
        """;
        await bot.SendTextMessageAsync(chatId, welcome);
        return;
    }
    else if (text == "/random")
    {
        if (IsRateLimited(quoteRequests, chatId))
        {
            await bot.SendTextMessageAsync(chatId, "⏳ Зачекай трохи перед наступною цитатою (макс 5 кожні 40 сек).");
            return;
        }

        if (!userSeenQuotes.ContainsKey(chatId))
            userSeenQuotes[chatId] = new HashSet<int>();

        var seen = userSeenQuotes[chatId];

        if (seen.Count >= MAX_QUOTES_PER_USER)
        {
            await bot.SendTextMessageAsync(chatId, "✅ Ви переглянули всі 50 цитат. Починаємо знову!");
            seen.Clear();
        }

        using var http = new HttpClient();
        QuoteResponse? quote = null;
        int retries = 0;

        while (retries < 20)
        {
            var apiUrl = $"https://motivation-quotes-api-production.up.railway.app/quotes/random?userId={chatId}";
            var response = await http.GetAsync(apiUrl);
            if (!response.IsSuccessStatusCode)
            {
                await bot.SendTextMessageAsync(chatId, "❌ Не вдалося отримати цитату.");
                return;
            }

            var json = await response.Content.ReadAsStringAsync();
            quote = JsonSerializer.Deserialize<QuoteResponse>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (quote != null && !seen.Contains(quote.Id))
                break;

            retries++;
        }

        if (quote == null)
        {
            await bot.SendTextMessageAsync(chatId, "⚠️ Не вдалося знайти нову цитату.");
            return;
        }

        seen.Add(quote.Id);
        lastQuote = quote;

        string message = $"💬 \"{quote.Text}\"\n— {quote.Author}\n\n👍 {quote.Likes}   👎 {quote.Dislikes}";

        var inlineKeyboard = new InlineKeyboardMarkup(new[]
        {
    new[]
    {
        InlineKeyboardButton.WithCallbackData("👍", $"like:{quote.Id}"),
        InlineKeyboardButton.WithCallbackData("👎", $"dislike:{quote.Id}")
    }
      });

        await bot.SendTextMessageAsync(chatId, message, replyMarkup: inlineKeyboard);
    }

    if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery != null)
    {
        var http = new HttpClient();

        var callbackData = update.CallbackQuery.Data;
        var callbackChatId = update.CallbackQuery.Message?.Chat.Id ?? 0;
        var userId = update.CallbackQuery.From.Id;

        if (!string.IsNullOrEmpty(callbackData) &&
            (callbackData.StartsWith("like:") || callbackData.StartsWith("dislike:")))
        {
            var parts = callbackData.Split(':');
            var reactionType = parts[0];
            var quoteId = int.Parse(parts[1]);

            var apiUrl = "https://motivation-quotes-api-production.up.railway.app/quotes/react";

            var payload = new
            {
                QuoteId = quoteId,
                UserId = userId,
                ReactionType = reactionType
            };

            var apiResponse = await http.PostAsJsonAsync(apiUrl, payload);

            if (apiResponse.IsSuccessStatusCode)
            {
                var result = await apiResponse.Content.ReadFromJsonAsync<ReactionResult>();
                var lines = update.CallbackQuery.Message?.Text?.Split('\n');
                if (lines == null || lines.Length < 2) return;

                var updatedText = $"{lines[0]}\n{lines[1]}\n\n👍 {result?.Likes ?? 0}   👎 {result?.Dislikes ?? 0}";
                try
                {
                    await bot.EditMessageTextAsync(
                        chatId: callbackChatId,
                        messageId: update.CallbackQuery.Message!.MessageId,
                        text: updatedText,
                        replyMarkup: update.CallbackQuery.Message.ReplyMarkup
                    );
                }
                catch (Telegram.Bot.Exceptions.ApiRequestException ex) when (ex.Message.Contains("message is not modified"))
                {
                    Console.WriteLine("⚠️ Повідомлення вже актуальне, редагування не потрібне.");
                }

                await bot.AnswerCallbackQueryAsync(update.CallbackQuery.Id);
            }
        }

        return;
    }

    else if (text == "/save")
    {
        if (!lastQuotes.TryGetValue(chatId, out var lastQuote) || lastQuote == null)
        {
            await bot.SendTextMessageAsync(chatId, "❗ Спочатку отримай цитату через /random.");
            return;
        }

        using var http = new HttpClient();
        var apiUrl = "https://motivation-quotes-api-production.up.railway.app/quotes/favorites/add";

        lastQuote.UserId = chatId;

        var quoteJson = JsonSerializer.Serialize(lastQuote);
        var content = new StringContent(quoteJson, Encoding.UTF8, "application/json");

        try
        {
            var response = await http.PostAsync(apiUrl, content);
            if (response.IsSuccessStatusCode)
            {
                await bot.SendTextMessageAsync(chatId, "✅ Цитату збережено в улюблені.");
            }
            else if ((int)response.StatusCode == 409)
            {
                await bot.SendTextMessageAsync(chatId, "⚠️ Цитата вже є в улюблених.");
            }
            else
            {
                await bot.SendTextMessageAsync(chatId, "❌ Не вдалося зберегти цитату.");
            }
        }
        catch (Exception ex)
        {
            await bot.SendTextMessageAsync(chatId, $"⚠️ Помилка: {ex.Message}");
        }
    }


    else if (text == "/favorites")
    {
        using var http = new HttpClient();
        var apiUrl = $"https://motivation-quotes-api-production.up.railway.app/quotes/favorites/list?userId={chatId}";

        try
        {
            var response = await http.GetAsync(apiUrl);
            var json = await response.Content.ReadAsStringAsync();

            var favorites = JsonSerializer.Deserialize<FavoritesResponse>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (favorites == null || favorites.Quotes.Count == 0)
            {
                await bot.SendTextMessageAsync(chatId, "🤷 У тебе ще немає улюблених цитат.");
                return;
            }

            foreach (var q in favorites.Quotes)
            {
                string msg = $"💬 \"{q.Text}\"\n— {q.Author} (ID: {q.Id})";
                await bot.SendTextMessageAsync(chatId, msg);
            }
        }
        catch (Exception ex)
        {
            await bot.SendTextMessageAsync(chatId, $"⚠️ Помилка: {ex.Message}");
        }
    }

    else if (text == "/delete")
    {
        waitingForDeleteId = true;
        await bot.SendTextMessageAsync(chatId, "✏️ Введи ID цитати, яку хочеш видалити:");
    }
    else if (waitingForDeleteId)
    {
        if (int.TryParse(text, out int id))
        {
            waitingForDeleteId = false;

            using var http = new HttpClient();
            var apiUrl = $"https://motivation-quotes-api-production.up.railway.app/quotes/favorites/delete/{id}?userId={chatId}";

            try
            {
                var response = await http.DeleteAsync(apiUrl);
                if (response.IsSuccessStatusCode)
                {
                    await bot.SendTextMessageAsync(chatId, "🗑️ Цитату видалено.");
                }
                else
                {
                    await bot.SendTextMessageAsync(chatId, "❌ Не вдалося видалити цитату.");
                }
            }
            catch (Exception ex)
            {
                await bot.SendTextMessageAsync(chatId, $"⚠️ Помилка: {ex.Message}");
            }
        }
    }


    else if (text == "/history")
    {
        using var http = new HttpClient();
        var apiUrl = $"https://motivation-quotes-api-production.up.railway.app/quotes/history?userId={chatId}";

        try
        {
            var response = await http.GetAsync(apiUrl);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                await bot.SendTextMessageAsync(chatId, "ℹ️ У вас поки немає збережених цитат в історії.");
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                await bot.SendTextMessageAsync(chatId, "❌ Не вдалося отримати історію цитат.");
                return;
            }

            var json = await response.Content.ReadAsStringAsync();
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var history = JsonSerializer.Deserialize<List<SearchHistory>>(json, options);

            if (history == null || history.Count == 0)
            {
                await bot.SendTextMessageAsync(chatId, "ℹ️ У вас поки немає збережених цитат в історії.");
                return;
            }

            var historyText = "🕓 Останні отримані цитати:\n\n";
            foreach (var item in history)
            {
                if (!string.IsNullOrWhiteSpace(item.Query))
                {
                    historyText += $"• {item.Query} ({item.SearchDate:dd.MM.yyyy HH:mm})\n";
                }
            }

            await bot.SendTextMessageAsync(chatId, historyText);
        }
        catch (Exception ex)
        {
            await bot.SendTextMessageAsync(chatId, $"⚠️ Помилка: {ex.Message}");
        }
    }

    else if (text == "/clear_history")
    {
        using var http = new HttpClient();
        var apiUrl = $"https://motivation-quotes-api-production.up.railway.app/quotes/history/clear?userId={chatId}";

        try
        {
            var response = await http.DeleteAsync(apiUrl);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                await bot.SendTextMessageAsync(chatId, "ℹ️ Історії пошуку цитат ще немає.");
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                await bot.SendTextMessageAsync(chatId, "❌ Не вдалося очистити історію.");
                return;
            }

            await bot.SendTextMessageAsync(chatId, "🧹 Історію пошуку успішно очищено.");
        }
        catch (Exception ex)
        {
            await bot.SendTextMessageAsync(chatId, $"⚠️ Помилка: {ex.Message}");
        }
    }


    else if (text == "/image")
    {
        if (IsRateLimited(imageRequests, chatId))
        {
            await bot.SendTextMessageAsync(chatId, "📷 Зачекай трохи перед наступною картинкою (макс 5 кожні 40 сек).");
            return;
        }

        var apiUrl = "https://motivation-quotes-api-production.up.railway.app/quotes/image";

        using var http = new HttpClient();
        var imageBytes = await http.GetByteArrayAsync(apiUrl);
        using var stream = new MemoryStream(imageBytes);
        InputOnlineFile input = new InputOnlineFile(stream, "quote.jpg");

        await bot.SendPhotoAsync(
            chatId: chatId,
            photo: input,
            caption: "🖼️ Ось надихаюча цитата у вигляді зображення:"
        );
    }


}
Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken token)
{
    Console.WriteLine($"❗ Помилка: {exception.Message}");
    return Task.CompletedTask;
}

public class FavoritesResponse
{
    public int Count { get; set; }
    public List<Quote> Quotes { get; set; } = new();
}
public class Quote
{
    public int Id { get; set; }
    public string Text { get; set; } = "";
    public string Author { get; set; } = "";
    public long UserId { get; set; }
}
public class SearchHistory
{
    public int Id { get; set; }
    public string Query { get; set; } = string.Empty;
    public DateTime SearchDate { get; set; }
    public long UserId { get; set; }
}
public class ApiMessage
{
    public string Message { get; set; } = string.Empty;
}
public class QuoteImageResponse
{
    public string Url { get; set; } = string.Empty;
}
public class ReactionResult
{

    public int Likes { get; set; }
    public int Dislikes { get; set; }
}
public class QuoteResponse
{
    public int Id { get; set; }
    public string Text { get; set; } = null!;
    public string Author { get; set; } = null!;
    public long UserId { get; set; }
    public int Likes { get; set; }
    public int Dislikes { get; set; }
}

