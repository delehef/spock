using System;
using System.Threading;
using Microsoft.SPOT;

namespace Spock
{
    class TestSubscriber : ISubscriber
    {
        public static void main()
        {
            Node node = Node.Instance;

            Debug.Print("Subscribing to string");
            node.subscribe("".GetType().Name, this);

            Thread.Sleep(3000);

            Debug.Print("Unsubscribing to string");
            node.unsubscribe("".GetType().Name, this);
        }

        public void receive(Object o)
        {
            Debug.Print("Receiving an object of type " + o.GetType().Name);
        }
    }
}
