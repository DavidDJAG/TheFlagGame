namespace TheFlag.Server;

public static class MapLoader
{
    public static GameMap LoadMapFromFile(string mapPath)
    {
        return GameRoom.LoadMapFromFile(mapPath);
    }

    public static GameMap LoadMapFromJson(string rawJson)
    {
        return GameRoom.LoadMapFromJson(rawJson);
    }
}
