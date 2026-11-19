namespace EFT;

public enum WildSpawnType
{
    pmcUSEC,
    pmcBEAR,
    assault,
    pmcBot,
    bossKilla,
    followerBully,
    exUsec,
    assaultGroup,
    custom,
}

public class Player
{
    public bool IsAI = true;
    public Profile? Profile = new();
}

public class Profile
{
    public Info? Info = new();
}

public class Info
{
    public Settings? Settings = new();
    public string? Nickname = "Scav",
        MainProfileNickname;
}

public class Settings
{
    public WildSpawnType Role;
}
