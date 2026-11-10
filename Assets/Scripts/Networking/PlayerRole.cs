namespace EscapeRoom.Networking
{
    /// <summary>
    /// Each of the three players gets exactly one impairment.
    /// </summary>
    public enum PlayerRole : byte
    {
        None = 0,
        Blind = 1,
        Deaf = 2,
        Mute = 3,
    }
}
