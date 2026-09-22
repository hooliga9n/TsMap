using System.Collections.Generic;
using System.Drawing;

namespace TsMap
{
    public class TsFerryPoint
    {
        public float X;
        public float Z;
        public double Rotation;

        public TsFerryPoint(float x, float z)
        {
            X = x;
            Z = z;
        }
        public void SetRotation(double rot)
        {
            Rotation = rot;
        }
    }

    public class TsFerryConnection
    {
        public ulong StartPortToken { get; set; }
        public PointF StartPortLocation { get; private set; }
        public ulong EndPortToken { get; set; }
        public PointF EndPortLocation { get; private set; }
        public List<TsFerryPoint> Connections = new List<TsFerryPoint>();

        /// <summary>Ticket price, in-game credits. Read from the "price" key of the connection's .sii block.</summary>
        public int Price { get; set; }
        /// <summary>Travel time, in in-game minutes. Read from the "time" key of the connection's .sii block.</summary>
        public int Time { get; set; }
        /// <summary>Travel distance, in km. Read from the "distance" key of the connection's .sii block.</summary>
        public double Distance { get; set; }

        /// <summary>
        /// The waypoints in between the two ports (excludes the start/end port positions
        /// themselves), in the same order they were declared in connection_positions[].
        /// Empty for short/direct crossings that only declare the two endpoints.
        /// </summary>
        public IEnumerable<TsFerryPoint> GetIntermediatePoints(bool reverse = false)
        {
            if (Connections.Count <= 2) yield break;
            if (!reverse)
            {
                for (var i = 1; i < Connections.Count - 1; i++) yield return Connections[i];
            }
            else
            {
                for (var i = Connections.Count - 2; i >= 1; i--) yield return Connections[i];
            }
        }

        public void AddConnectionPosition(int index, float x, float z)
        {
            if (index < 0) return;
            while (Connections.Count <= index)
            {
                Connections.Add(new TsFerryPoint(0, 0));
            }
            Connections[index] = new TsFerryPoint(x / 256, z / 256);
        }
        public void AddRotation(int index, double rot)
        {
            if (index < 0 || index >= Connections.Count) return;
            Connections[index].SetRotation(rot);
        }

        public void SetPortLocation(ulong ferryPortId, float x, float z)
        {
            if (ferryPortId == StartPortToken)
            {
                StartPortLocation = new PointF(x, z);
            }
            else if (ferryPortId == EndPortToken)
            {
                EndPortLocation = new PointF(x, z);
            }
        }
    }
}
