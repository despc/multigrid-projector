using VRage;
using VRage.Game.Components;
using VRageMath;

namespace MultigridProjector.Extensions
{
    public static class LinearAlgebraExtensions
    {
        public static MyPositionAndOrientation ToPositionAndOrientation(this MatrixD matrix)
        {
            return new MyPositionAndOrientation(matrix.Translation, matrix.Forward, matrix.Up);
        }

        public static void ToMatrixD(this MyPositionAndOrientation po, out MatrixD matrix)
        {
            matrix = MatrixD.CreateWorld(po.Position, po.Orientation.Forward, po.Orientation.Up);
        }

        // How far a preview grid's matrix may be from the one worked out for it and still be left as it is: a millimetre,
        // and a turn that moves a point 100 m off by as much - below what can be seen or what a block's placement check
        // notices (a cube is 0.5 or 2.5 m). The game itself leaves a matrix only within 1e-6 of each element.
        private const double SamePositionMeters = 1e-3;
        private const double SameOrientation = 1e-5;

        /// <summary>
        /// Sets the world matrix unless it is that already, within a millimetre. The previews are aligned every frame,
        /// and setting a grid's matrix moves every block of it; a ship at rest trembles in its physics by fractions of a
        /// millimetre, and its projections were moved with it, every block, every frame (prod 06.11.2026: 9 ms a second
        /// of the server's game thread in moving the previews, the most of all the projector's work). The preview is
        /// never further than that from where it is to be: it is set again once it would be.
        /// </summary>
        public static void SetWorldMatrixIfMoved(this MyPositionComponentBase position, ref MatrixD worldMatrix)
        {
            if (IsSame(in position.WorldMatrixRef, in worldMatrix))
                return;

            position.SetWorldMatrix(ref worldMatrix, skipTeleportCheck: true);
        }

        public static bool IsSame(in MatrixD a, in MatrixD b)
        {
            return Vector3D.DistanceSquared(a.Translation, b.Translation) <= SamePositionMeters * SamePositionMeters &&
                   Vector3D.DistanceSquared(a.Forward, b.Forward) <= SameOrientation * SameOrientation &&
                   Vector3D.DistanceSquared(a.Up, b.Up) <= SameOrientation * SameOrientation &&
                   Vector3D.DistanceSquared(a.Right, b.Right) <= SameOrientation * SameOrientation;
        }

        public static string FormatYaml(this Vector3I v)
        {
            return $"{v.X}, {v.Y}, {v.Z}";
        }
    }
}