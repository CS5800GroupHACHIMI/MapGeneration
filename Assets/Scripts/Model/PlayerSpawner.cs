namespace Model
{
    public class PlayerSpawner
    {
        private readonly MapGrid _grid;
        private readonly MapConfig _config;
        private readonly Player _player;
        
        public PlayerSpawner(MapGrid grid, MapConfig config, Player player)
        {
            _grid   = grid;
            _config = config;
            _player = player;
        }
        
        public void Spawn()
        {
            int x = _grid.Width  / 2;
            int y = _grid.Height / 2;
            _player.MoveTo(x, y);
        }
    }
}