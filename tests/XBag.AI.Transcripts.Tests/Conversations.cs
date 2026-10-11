namespace XBag.AI.Transcripts.Tests;

/// <summary>Message builders, in the shapes FunctionInvokingChatClient and the history stores produce.</summary>
internal static class Msg
{
    public static ChatMessage System(string text) => new(ChatRole.System, text);

    public static ChatMessage User(string text) => new(ChatRole.User, text);

    public static ChatMessage Assistant(string text) => new(ChatRole.Assistant, text);

    public static FunctionCallContent Call(string callId, string name, object? arguments = null) =>
        new(callId, name, arguments is null ? null : ToolArgs(arguments));

    public static ChatMessage Calls(params FunctionCallContent[] calls) => new(ChatRole.Assistant, [.. calls]);

    public static ChatMessage Result(string callId, object? result) => new(ChatRole.Tool, [new FunctionResultContent(callId, result)]);

    /// <summary>What FunctionInvokingChatClient returns for a call to an approval-required tool (request id "ficc_" + call id).</summary>
    public static ChatMessage ApprovalRequest(string callId, string name, object? arguments = null) =>
        new(ChatRole.Assistant, [new ToolApprovalRequestContent("ficc_" + callId, Call(callId, name, arguments))]);

    /// <summary>The user's answer, as an app sends it back (in a user message).</summary>
    public static ChatMessage ApprovalResponse(string callId, string name, bool approved, object? arguments = null) =>
        new(ChatRole.User, [new ToolApprovalResponseContent("ficc_" + callId, approved, Call(callId, name, arguments))]);

    private static Dictionary<string, object?> ToolArgs(object arguments) =>
        arguments.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(arguments));
}

/// <summary>Broken transcripts as they come out of real failures, plus healthy ones.</summary>
internal static class Conversations
{
    /// <summary>The process stopped after the model asked for the cargo tool and before the tool answered; the user writes again.</summary>
    public static List<ChatMessage> CrashBetweenCallAndResult() =>
    [
        Msg.System("Sen bir e-ticaret destek asistanısın."),
        Msg.User("Siparişim nerede? Takip numarası TR123456789."),
        Msg.Calls(Msg.Call("call_1", "kargo_takip", new { takip_no = "TR123456789" })),
        Msg.User("Hâlâ bekliyorum, ne oldu?"),
    ];

    /// <summary>The user never answered the approval for a transfer, and asked something else instead.</summary>
    public static List<ChatMessage> ApprovalNeverAnswered() =>
    [
        Msg.User("Ali Yılmaz'a 500 TL gönder."),
        Msg.ApprovalRequest("call_1", "para_gonder", new { alici = "Ali Yılmaz", tutar = 500 }),
        Msg.User("Vazgeçtim. Bakiyem ne kadar?"),
    ];

    /// <summary>
    /// The run after the user's "yes" failed before the transfer, the app saved an apology, and a message-count reducer later
    /// trimmed the approval request away. The answer stays behind in the history, never carried out.
    /// </summary>
    public static List<ChatMessage> ApprovalWithoutRequest() =>
    [
        Msg.User("Mehmet Kaya'ya 9000 TL gönder."),
        Msg.ApprovalResponse("call_9", "para_gonder", approved: true, new { alici = "Mehmet Kaya", tutar = 9000 }),
        Msg.Assistant("Bir hata oluştu, lütfen daha sonra tekrar deneyin."),
        Msg.User("Boş ver. Bakiyem ne kadar?"),
    ];

    /// <summary>A context provider injected a user message between the call and its result (agent-framework #6953).</summary>
    public static List<ChatMessage> MessageBetweenCallAndResult() =>
    [
        Msg.User("Ankara'da yarın hava nasıl?"),
        Msg.Calls(Msg.Call("call_1", "hava_durumu", new { sehir = "Ankara" })),
        Msg.User("[Hatırlatma: kullanıcı premium üye.]"),
        Msg.Result("call_1", "Ankara yarın 18°C, parçalı bulutlu."),
        Msg.Assistant("Ankara'da yarın 18°C ve parçalı bulutlu."),
    ];

    /// <summary>A resumed run appended the tool result at the end (agent-framework #6909).</summary>
    public static List<ChatMessage> ResultAppendedAtTheEnd() =>
    [
        Msg.User("İzmir'e yarın için otel bul."),
        Msg.Calls(Msg.Call("call_1", "otel_ara", new { sehir = "İzmir" })),
        Msg.User("Deniz manzaralı olsun."),
        Msg.Result("call_1", "3 otel bulundu: Kordon Otel, Alsancak Suites, Karşıyaka Inn."),
    ];

    /// <summary>A reducer dropped the assistant message with the call but kept its result (agent-framework #4494).</summary>
    public static List<ChatMessage> ReducerDroppedTheCall() =>
    [
        Msg.Result("call_1", "{\"kur\":41.2}"),
        Msg.Assistant("1 dolar 41,2 TL."),
        Msg.User("Peki euro?"),
    ];

    /// <summary>Two parallel calls, the stream was cancelled after the first result: one result missing, an empty assistant message.</summary>
    public static List<ChatMessage> CancelledStream() =>
    [
        Msg.User("İstanbul'dan Ankara'ya yarın uçuş ve tren var mı?"),
        new(ChatRole.Assistant, [new TextContent("Bakıyorum."), Msg.Call("call_1", "ucus_ara", new { nereden = "IST", nereye = "ESB" }), Msg.Call("call_2", "tren_ara", new { nereden = "İstanbul", nereye = "Ankara" })]),
        Msg.Result("call_1", "Yarın 6 uçuş var, en ucuzu 1.250 TL."),
        new(ChatRole.Assistant, []),
        Msg.User("Cevap gelmedi, tekrar dener misin?"),
    ];

    /// <summary>A store wrote the same tool turn twice (a retried save).</summary>
    public static List<ChatMessage> SavedTwice() =>
    [
        Msg.User("Kadıköy'de eczane nöbetçi mi?"),
        Msg.Calls(Msg.Call("call_1", "nobetci_eczane", new { ilce = "Kadıköy" })),
        Msg.Result("call_1", "Moda Eczanesi nöbetçi."),
        Msg.Calls(Msg.Call("call_1", "nobetci_eczane", new { ilce = "Kadıköy" })),
        Msg.Result("call_1", "Moda Eczanesi nöbetçi."),
        Msg.Assistant("Moda Eczanesi bu gece nöbetçi."),
    ];

    /// <summary>Several conversations in one list, with the call and request ids of part n prefixed "pn_" so they cannot collide.</summary>
    public static List<ChatMessage> Combine(params List<ChatMessage>[] parts) =>
        parts.SelectMany((part, n) => part.Select(message => Prefixed(message, $"p{n + 1}_"))).ToList();

    private static ChatMessage Prefixed(ChatMessage message, string prefix)
    {
        static FunctionCallContent Call(ToolCallContent call, string prefix) =>
            new(prefix + call.CallId, ((FunctionCallContent)call).Name, ((FunctionCallContent)call).Arguments);

        ChatMessage copy = message.Clone();
        copy.Contents = message.Contents.Select(content => content switch
        {
            FunctionCallContent call => Call(call, prefix),
            FunctionResultContent result => new FunctionResultContent(prefix + result.CallId, result.Result),
            ToolApprovalRequestContent request => new ToolApprovalRequestContent(prefix + request.RequestId, Call(request.ToolCall, prefix)),
            ToolApprovalResponseContent response => new ToolApprovalResponseContent(prefix + response.RequestId, response.Approved, Call(response.ToolCall, prefix)),
            _ => content,
        }).ToList();
        return copy;
    }

    /// <summary>A healthy tool turn with two parallel calls.</summary>
    public static List<ChatMessage> Healthy() =>
    [
        Msg.System("Sen bir seyahat asistanısın."),
        Msg.User("Antalya ve Muğla'da hava nasıl?"),
        Msg.Calls(Msg.Call("call_1", "hava_durumu", new { sehir = "Antalya" }), Msg.Call("call_2", "hava_durumu", new { sehir = "Muğla" })),
        Msg.Result("call_1", "Antalya 29°C, güneşli."),
        Msg.Result("call_2", "Muğla 26°C, açık."),
        Msg.Assistant("Antalya 29°C güneşli, Muğla 26°C açık."),
        Msg.User("Teşekkürler!"),
    ];
}
