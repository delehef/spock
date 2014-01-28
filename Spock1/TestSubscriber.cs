using System;
using System.Threading;
#if MF
using Microsoft.SPOT;
#endif
using System.Diagnostics;

namespace Spock
{
    class TestSubscriber : ISubscriber
    {
        public void test()
        {
            try
            {
                Node node = Node.Instance;

                Debug.Print("Subscribing to string");
                node.subscribe("".GetType(), this);

                Thread.Sleep(10000);

                Debug.Print("Unsubscribing to string");
                node.unsubscribe("".GetType(), this);
            }
            catch (Exception e)
            {
                Debug.Print(e.Message);
                Debug.Print(e.StackTrace);
            }
        }

        public void receive(Object o)
        {
            Debug.Print("Receiving an object of type " + o.GetType().Name);
        }
    }
}
