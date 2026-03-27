namespace Generators
{
    // Returns a spanning tree of the input graph using Prim's algorithm.
    // Since Graph uses unweighted edges, all edges are treated as weight 1.
    // The result is a new Graph containing only the spanning tree edges.
    public static class Prim
    {
        public static Graph SpanningTree(Graph graph)
        {
            var tree   = new Graph(graph.Vertices);
            var inTree = new bool[graph.Vertices];

            // Each entry is (from, to) — a candidate edge crossing into the frontier
            var frontier = new List<(int from, int to)>();

            // Start from vertex 0
            inTree[0] = true;
            AddFrontierEdges(graph, frontier, inTree, 0);

            while (frontier.Count > 0)
            {
                // Pick any frontier edge (index 0 works; all weights are equal)
                var (from, to) = frontier[0];
                frontier.RemoveAt(0);

                // Skip if both ends are already in the tree (would create a cycle)
                if (inTree[to]) continue;

                tree.AddEdge(from, to);
                inTree[to] = true;
                AddFrontierEdges(graph, frontier, inTree, to);
            }

            return tree;
        }

        private static void AddFrontierEdges(
            Graph graph, List<(int, int)> frontier, bool[] inTree, int vertex)
        {
            foreach (int neighbor in graph.GetNeighbors(vertex))
                if (!inTree[neighbor])
                    frontier.Add((vertex, neighbor));
        }
    }
}
