namespace Editor.TeamCreate;

public sealed class Message
{
	[JsonPropertyName( "type" )] public string Type { get; set; } = "";
	[JsonPropertyName( "name" )] public string? Name { get; set; }
	[JsonPropertyName( "color" )] public string? Color { get; set; }
	[JsonPropertyName( "room" )] public string? Room { get; set; }
	[JsonPropertyName( "passwordHash" )] public string? PasswordHash { get; set; }
	[JsonPropertyName( "from" )] public string? From { get; set; }
	[JsonPropertyName( "peers" )] public List<PeerInfo>? Peers { get; set; }
	[JsonPropertyName( "peer" )] public PeerInfo? Peer { get; set; }
	[JsonPropertyName( "path" )] public string? Path { get; set; }
	[JsonPropertyName( "contentB64" )] public string? ContentB64 { get; set; }
	[JsonPropertyName( "camPos" )] public float[]? CamPos { get; set; }
	[JsonPropertyName( "camRot" )] public float[]? CamRot { get; set; }
	[JsonPropertyName( "selection" )] public List<string>? Selection { get; set; }
	[JsonPropertyName( "guid" )] public string? Guid { get; set; }
	[JsonPropertyName( "pos" )] public float[]? Pos { get; set; }
	[JsonPropertyName( "rot" )] public float[]? Rot { get; set; }
	[JsonPropertyName( "scale" )] public float[]? Scale { get; set; }
	[JsonPropertyName( "reason" )] public string? Reason { get; set; }

	public static readonly JsonSerializerOptions JsonOptions = new()
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	public string ToJson() => JsonSerializer.Serialize( this, JsonOptions );

	public static Message? FromJson( string json )
	{
		try { return JsonSerializer.Deserialize<Message>( json, JsonOptions ); }
		catch { return null; }
	}
}

public sealed class PeerInfo
{
	[JsonPropertyName( "id" )] public string Id { get; set; } = "";
	[JsonPropertyName( "name" )] public string Name { get; set; } = "";
	[JsonPropertyName( "color" )] public string Color { get; set; } = "#ffffff";
}
