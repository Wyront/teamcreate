using System.Text.Json;
using System.Text.Json.Serialization;

namespace TeamCreate.Hub;

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
	// Protocol v2: chunked files, presence, scene locks
	[JsonPropertyName( "chunkIndex" )] public int? ChunkIndex { get; set; }
	[JsonPropertyName( "chunkTotal" )] public int? ChunkTotal { get; set; }
	[JsonPropertyName( "fileSize" )] public long? FileSize { get; set; }
	[JsonPropertyName( "fileHash" )] public string? FileHash { get; set; }
	[JsonPropertyName( "final" )] public bool? Final { get; set; }
	[JsonPropertyName( "scene" )] public string? Scene { get; set; }
	[JsonPropertyName( "locks" )] public List<SceneLockInfo>? Locks { get; set; }

	public string ToJson() => JsonSerializer.Serialize( this, HubJsonContext.Default.Message );

	public static Message? FromJson( string json )
	{
		try { return JsonSerializer.Deserialize( json, HubJsonContext.Default.Message ); }
		catch { return null; }
	}
}

[JsonSourceGenerationOptions( DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull )]
[JsonSerializable( typeof( Message ) )]
[JsonSerializable( typeof( List<PeerInfo> ) )]
[JsonSerializable( typeof( List<SceneLockInfo> ) )]
internal partial class HubJsonContext : JsonSerializerContext
{
}

public sealed class PeerInfo
{
	[JsonPropertyName( "id" )] public string Id { get; set; } = "";
	[JsonPropertyName( "name" )] public string Name { get; set; } = "";
	[JsonPropertyName( "color" )] public string Color { get; set; } = "#ffffff";
}

public sealed class SceneLockInfo
{
	[JsonPropertyName( "path" )] public string Path { get; set; } = "";
	[JsonPropertyName( "owner" )] public string Owner { get; set; } = "";
	[JsonPropertyName( "ownerName" )] public string OwnerName { get; set; } = "";
}
