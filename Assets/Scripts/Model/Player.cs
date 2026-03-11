using System;

namespace Model
{
    public class Player
    {
        public int X { get; private set; }
        public int Y { get; private set; }
        
        public event Action<int, int> OnMoved;
        public event Action<int, int> OnTeleported;
        
        public Player() { }

        public void MoveTo(int x, int y)
        {
            X = x;
            Y = y;
            OnMoved?.Invoke(x, y);
        }

        public void TeleportTo(int x, int y)
        {
            X = x;
            Y = y;
            OnTeleported?.Invoke(x, y);
        }
    }
}