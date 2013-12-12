using System;
using Microsoft.SPOT;

namespace Spock
{
    class Utils
    {
        public static int ToInt32(byte[] value, int index = 0)
        {
            return (
                value[0 + index] << 0 |
                value[1 + index] << 8 |
                value[2 + index] << 16 |
                value[3 + index] << 24);
        }
    }
}
