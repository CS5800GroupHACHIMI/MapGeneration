using System;

namespace Model
{
    public class Player
    {
        public int X { get; private set; }
        public int Y { get; private set; }

        public int   MaxHealth { get; private set; } = 100;
        public int   Health    { get; private set; } = 100;
        public bool  IsDead    => Health <= 0;

        public event Action<int, int> OnMoved;
        public event Action<int, int> OnTeleported;
        public event Action<int>      OnHealthChanged; // current HP
        public event Action           OnDied;

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

        public void TakeDamage(int amount)
        {
            if (IsDead) return;
            Health = Math.Max(0, Health - amount);
            OnHealthChanged?.Invoke(Health);
            if (Health <= 0) OnDied?.Invoke();
        }

        public void ResetHealth()
        {
            Health = MaxHealth;
            OnHealthChanged?.Invoke(Health);
        }
    }
}
