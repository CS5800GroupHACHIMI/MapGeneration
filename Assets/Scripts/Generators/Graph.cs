using System.Collections.Generic;

namespace Generators
{
    public class Graph(int vertices = 5)
    {
        // using adjacency matrix for easier update when adding and removing undirected edges
        private readonly bool[,] _adjacencyMatrix = new bool[vertices, vertices];
        public int Vertices { get; } = vertices;


        // undirected edge in the graph, helper method
        public void AddEdge(int a, int b)
        {
            _adjacencyMatrix[a, b] = true;
            _adjacencyMatrix[b, a] = true;
        }

        public bool HasEdge(int a, int b) => _adjacencyMatrix[a, b];

        public List<int> GetNeighbors(int vertex)
        {
            var neighbors = new List<int>();
            for (int i = 0; i < Vertices; i++)
                if (_adjacencyMatrix[vertex, i])
                    neighbors.Add(i);
            return neighbors;
        }

        public static Graph CreateRandom(int vertices = 5)
        {
            var graph = new Graph(vertices);

            // this can be changed for maybe a fraction of edges compared given vertices
            int maxEdges = vertices * (vertices - 1) / 2;
            int numEdges = Random.Range(vertices - 1, maxEdges + 1);

            // Collect all valid undirected pairs (no self-loops)
            var pairs = new List<(int, int)>();
            for (int i = 0; i < vertices; i++)
                for (int j = i + 1; j < vertices; j++)
                    pairs.Add((i, j));

            // Shuffle and pick numEdges pairs
            for (int i = pairs.Count - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                (pairs[i], pairs[j]) = (pairs[j], pairs[i]);
            }

            for (int k = 0; k < numEdges && k < pairs.Count; k++)

                graph.AddEdge(pairs[k].Item1, pairs[k].Item2);

            return graph;
        }
    }
}
