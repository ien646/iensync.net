namespace iensync
{
    public class SyncEntry
    {
        public string Host { get; set; } = "";
        public ushort Port { get; set; } = 0;
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public string RemoteDir { get; set; } = "";
        public string LocalDir { get; set; } = "";
        public bool SyncToRemote { get; set; } = false;
        public bool SyncToLocal { get; set; } = false;
    }
}
