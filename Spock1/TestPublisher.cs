using System;
using Microsoft.SPOT;

namespace Spock
{
    class TestPublisher
    {
        public static void main()
        {
            Node node = Node.Instance;
            int i = 0;

            while (true)
            {
                node.publish("KIKOOLOL #" + i++);
                System.Threading.Thread.Sleep(1000);
            }
        }
    }
}
