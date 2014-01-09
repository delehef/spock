using System;
using Microsoft.SPOT;

namespace Spock
{
    class TestPublisher
    {
        public static void test()
        {
            try
            {
                Node node = Node.Instance;
                int i = 0;

                while (true)
                {
                    node.publish("KIKOOLOL #" + i++);
                    System.Threading.Thread.Sleep(1000);
                }
            }
            catch (Exception e)
            {
                Debug.Print(e.Message);
                Debug.Print(e.StackTrace);
            }

        }
    }
}
