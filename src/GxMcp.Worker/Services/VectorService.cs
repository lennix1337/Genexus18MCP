using System;
using System.Collections.Generic;
using System.Linq;

namespace GxMcp.Worker.Services
{
    public class VectorService
    {
        // Simple TF-IDF inspired local embedding for GeneXus objects
        // This is a zero-dependency semantic bridge.

        // PERFORMANCE (W-A2): hoist separator array to a static field so each call doesn't
        // allocate a fresh char[]. The output float[128] cannot be pooled because the caller
        // persists it on IndexEntry.Embedding (serialized to disk and reused for similarity
        // scoring), so any pooled buffer would have indeterminate lifetime.
        private static readonly char[] WordSeparators = { ' ', '.', ',', '(', ')', '[', ']', ':', ';' };

        public float[] ComputeEmbedding(string text)
        {
            if (string.IsNullOrEmpty(text)) return new float[128];

            // We use a fixed-size hashing vector for local comparison (SimHash-like)
            float[] vector = new float[128];

            // PERFORMANCE: all 4 quadrants of the 128-dim vector receive the exact same
            // 32-bit SimHash accumulation, so compute and normalize only the first 32 floats
            // and replicate the normalized slice 3 times via Buffer.BlockCopy (4x fewer ops).
            // Skip rawWord.ToLowerInvariant() allocation when rawWord is already lowercase ASCII.
            var words = text.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries);
            for (int w = 0; w < words.Length; w++)
            {
                string rawWord = words[w];
                bool needsLower = false;
                for (int c = 0; c < rawWord.Length; c++)
                {
                    char ch = rawWord[c];
                    if (ch >= 128 || (ch >= 'A' && ch <= 'Z'))
                    {
                        needsLower = true;
                        break;
                    }
                }
                int hash = (needsLower ? rawWord.ToLowerInvariant() : rawWord).GetHashCode();
                for (int bit = 0; bit < 32; bit++)
                {
                    vector[bit] += ((hash >> bit) & 1) == 1 ? 1.0f : -1.0f;
                }
            }

            // Normalize across all 4 identical 32-float quadrants: sum128 == 4 * sum32.
            float sum32 = 0;
            for (int i = 0; i < 32; i++)
            {
                float v = vector[i];
                sum32 += v * v;
            }
            float magnitude = (float)Math.Sqrt(sum32 * 4.0f);

            if (magnitude > 0)
            {
                float invMag = 1.0f / magnitude;
                for (int i = 0; i < 32; i++) vector[i] *= invMag;
            }

            const int QuadrantBytes = 32 * sizeof(float);
            Buffer.BlockCopy(vector, 0, vector, QuadrantBytes, QuadrantBytes);
            Buffer.BlockCopy(vector, 0, vector, QuadrantBytes * 2, QuadrantBytes * 2);

            return vector;
        }

        // Extremely fast dot product for normalized vectors using pointer arithmetic and loop unrolling.
        public unsafe float CosineSimilarity(float[] v1, float[] v2)
        {
            if (v1 == null || v2 == null || v1.Length != v2.Length) return 0f;

            float dotProduct = 0f;
            int length = v1.Length;

            fixed (float* p1 = v1)
            fixed (float* p2 = v2)
            {
                float* p1a = p1;
                float* p2a = p2;
                float* end = p1 + length;
                float* endUnrolled = p1 + (length - (length % 8));

                // Process in chunks of 8 to minimize loop overhead and allow superscalar execution
                while (p1a < endUnrolled)
                {
                    dotProduct += (p1a[0] * p2a[0]) + (p1a[1] * p2a[1]) +
                                  (p1a[2] * p2a[2]) + (p1a[3] * p2a[3]) +
                                  (p1a[4] * p2a[4]) + (p1a[5] * p2a[5]) +
                                  (p1a[6] * p2a[6]) + (p1a[7] * p2a[7]);

                    p1a += 8;
                    p2a += 8;
                }

                // Process the remaining elements
                while (p1a < end)
                {
                    dotProduct += (*p1a) * (*p2a);
                    p1a++;
                    p2a++;
                }
            }

            return dotProduct;
        }
    }
}
