using System;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Collections;
using Microsoft.SPOT;
using Microsoft.SPOT.Hardware;
using SecretLabs.NETMF.Hardware;
using SecretLabs.NETMF.Hardware.Netduino;
using Microsoft.SPOT.Net.NetworkInformation;
using System.Diagnostics;

namespace Spock
{
    public class Node
    {
        // The node singleton for this board
        private static readonly Node instance = new Node();

        // CONSTANTS
        private const int BROADCAST_MSG_SIZE = 9;
        private const int PAYLOAD_OFFSET = 5;
        private const int TCP_MAX_TRIES = 5;
        private const int TCP_TIMEOUT = 10000;


        // MEMBERS
        // The object waiting to be published
        private readonly object currentObjectLock = new object();
        private object currentObject;

        // Dictionary {objectType: [interested remotes]}
        private readonly object objectToRemoteClientLock = new object();
        private Hashtable objectToRemoteClient = new Hashtable();

        // Dictionary {objectType: [local subscribers]}
        private readonly object objectToLocalClientLock = new object();
        private Hashtable objectToLocalClient = new Hashtable();

        Socket socketSend;     // used to send requests or objects over TCP
        Socket socketReceive;  // used to receive requests or objects

        // CONSTRUCTORS
        public Node()
        {
            var networkInterfaces = NetworkInterface.GetAllNetworkInterfaces();
            Debug.Assert(networkInterfaces[0] != null);
            var net = networkInterfaces[0];

            Debug.Print("DHCP Enabled: " + net.IsDhcpEnabled);
            Debug.Print("IP Address: " + net.IPAddress);


            Thread broadThread = new Thread(new ThreadStart(broadcast));
            //broadThread.Start();

            Thread listenThread = new Thread(new ThreadStart(listenBroadcast));
            listenThread.Start();

            try
            {
                socketReceive = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socketReceive.Bind(new IPEndPoint(IPAddress.Any, 4321));
                socketReceive.Listen(100); // param : size of the pending connections queue
            }
            catch (Exception e)
            {
                Debug.Print(e.StackTrace);
                Debug.Print(e.Message);
            }

            Debug.Print("TCP server launched");
            Thread TCPListenThread = new Thread(new ThreadStart(listenForRequest));
            TCPListenThread.Start();
        }


        // SINGLETON
        public static Node Instance
        {
            get
            {
                return instance;
            }
        }


        // IMPLEMENTATION
        // PRIVATE
        private byte[] readExactSize(Socket s, int size)
        {
            byte[] buffer = new byte[size];
            int receivedSize = 0;

            while (receivedSize < size)
                receivedSize += s.Receive(buffer, receivedSize, size - receivedSize, SocketFlags.None);

            return buffer;
        }


        /**
         * Listen the broadcast requests and process them
         */
        private void listenBroadcast()
        {
            Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            EndPoint ep = (EndPoint)(new IPEndPoint(IPAddress.Any, 1234));
            socket.Bind(ep);

            bool isAlive = true;
            try
            {
                while (isAlive)
                {
                    string msg = "";
                    if (socket.Available >= BROADCAST_MSG_SIZE)
                    {
                        byte[] buffer = new byte[BROADCAST_MSG_SIZE];
                        // Read the first byte, aka type of message
                        int nbReceived = socket.Receive(buffer, 9, SocketFlags.None);
                        msg = new string(Encoding.UTF8.GetChars(buffer));
                        Debug.Print("\nReceived data: " + msg + " for " + nbReceived.ToString() + "B");
                        switch (buffer[0])
                        {
                            //  asks for a type of object
                            case (byte)'A':
                                {
                                    string IP = buffer[1] + "." + buffer[2] + "." + buffer[3] + "." + buffer[4];
                                    int objectType = BitConverter.ToInt32(buffer, PAYLOAD_OFFSET);
                                    Debug.Print(IP + " asks for the object #" + objectType);
                                    break;
                                }


                            // offer a type of object
                            case (byte)'O':
                                {
                                    string IP = buffer[1] + "." + buffer[2] + "." + buffer[3] + "." + buffer[4];
                                    int objectType = BitConverter.ToInt32(buffer, 5);
                                    Debug.Print(IP + " offers the object #" + objectType);
                                    break;
                                }

                            default:
                                Debug.Print("Unknown command : " + buffer[0].ToString());
                                break;
                        }
                    }
                    Thread.Sleep(500);
                }
            }
            catch (Exception exc)
            {
                Debug.Print("Exception received while listening to broadcast: " + exc.Message);
            }
        }


        /**
         * Called when a new object is received from the network
         */
        private void receiveFromNetwork(Object o)
        {
            string className = o.GetType().Name;
            Debug.Print("We juste received a " + className + " from the network");

            // Transmit to the concerned locals
            deliverToLocals(o);
        }



        /**
         * Dictribute the object o to all the concerned locals clients
         */
        private void deliverToLocals(Object o)
        {
            string className = o.GetType().Name;
            lock (objectToLocalClientLock)
            {
                ArrayList localsList = (ArrayList)objectToLocalClient[className];
                if (localsList == null || localsList.Count == 0)
                    Debug.Print("No local cares about your stupid " + className + "!");
                else
                    foreach (ISubscriber s in localsList)
                    {
                        Debug.Print("Notifying a local of a " + className);
                        s.receive(o);
                    }
            }
        }



        /**
         * Dictribute the object o to all the concerned remotes clients
         */
        private void deliverToRemotes(object o)
        {
            string className = o.GetType().Name;
            lock (objectToRemoteClientLock)
            {
                ArrayList remotesList = (ArrayList)objectToRemoteClient[className];
                if (remotesList == null || remotesList.Count == 0)
                    Debug.Print("No remote cares about your stupid " + className + "!");
                else
                    foreach (string address in remotesList)
                    {
                        Debug.Print("Notifying " + address + " of a " + className);
                        sendObject(address, o);
                    }
            }
        }



        /**
         * Called when a new object is received from one of the local clients
         */
        private void receiveFromLocal(Object o)
        {
            string className = o.GetType().Name;
            Debug.Print("We distribute a " + className + " from the local client");

            // Transmit to the concerned local...
            deliverToLocals(o);
            // ...then to the concerned remotes
            deliverToRemotes(o);
        }


        /**
         * Send the object o to the remote client at IPAddress
         */
        private void sendObject(string IPAddress, Object o)
        {

            int startTime = System.DateTime.Now.Millisecond;
            byte[] toSend = { }; // TODO : serialize o
            int sent = 0;
            int nbTries = 0;

            while (sent < toSend.Length)
            {
                if (System.DateTime.Now.Millisecond > startTime + TCP_TIMEOUT)
                    return;
                try
                {
                    sent += socketSend.Send(toSend, sent, toSend.Length - sent, SocketFlags.None);
                }
                catch (SocketException ex)
                {
                    if (nbTries < TCP_MAX_TRIES)
                    {
                        nbTries++;
                        Thread.Sleep(30);
                    }
                    else
                        throw ex;
                }
            }
        }


        /**
         * We got a new local subscriber for t, we got to ask for some t in the network
         */
        private void remotelySubscribe(ISubscriber subscriber, Type t)
        {
            // TODO
        }


        /**
         * Take in account a new local subscriber
         */
        private void locallySubscribe(ISubscriber subscriber, Type t)
        {
            lock (objectToLocalClientLock)
            {
                ArrayList currentClients = (ArrayList)objectToLocalClient[t.Name];
                
                if (currentClients == null)
                    currentClients = new ArrayList();
                currentClients.Add(subscriber);

                objectToLocalClient[t.Name] = currentClients;
            }
        }


        
        /**
         * If only subscriber asks for t, we need to tell the network it's over for us
         */
        private void remotelyUnsubscribe(ISubscriber subscriber, Type t)
        {
            // TODO
        }



        /**
         * subscriber doesn't ask for t anymore
         */
        private void locallyUnsubscribe(ISubscriber subscriber, Type t)
        {
            lock (objectToLocalClientLock)
            {
                ArrayList a = (ArrayList)objectToLocalClient[t.Name];
                a.Remove(subscriber);
                objectToLocalClient[t.Name] = a;
                //((ArrayList)objectToLocalClient[t.Name]).Remove(subscriber);
            }
        }



        // PUBLIC
        /**
         * Listen for a TCP transmission, either a request or an object
         */
        public void listenForRequest()
        {
            Debug.Print("Listening for request");
            while (true)
            {
                try
                {
                    using (Socket clientSocket = socketReceive.Accept())
                    {
                        Debug.Print("New connection");
                        //Get client's IP
                        IPEndPoint clientIP = clientSocket.RemoteEndPoint as IPEndPoint;
                        EndPoint clientEndPoint = clientSocket.RemoteEndPoint;

                        // Read the message size
                        int msgSize = (int)BitConverter.ToUInt32(readExactSize(clientSocket, sizeof(int)));
                        Debug.Print("Receiving a message of " + msgSize.ToString() + "B");

                        // Read the message itself
                        byte[] msg = readExactSize(clientSocket, msgSize);

                        string request = new string(System.Text.Encoding.UTF8.GetChars(msg));
                        Debug.Print(request);

                        //Compose a response
                        Debug.Print("Sending a response");
                        byte[] response = System.Text.Encoding.UTF8.GetBytes("tableau de char");
                        clientSocket.Send(response, response.Length, SocketFlags.None);
                    }
                }
                catch (Exception e)
                {
                    Debug.Print(e.StackTrace);
                    Debug.Print(e.Message);
                }
            }
        }


        public void broadcast()
        {
            try
            {
                Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true); // Enable broadcast 

                byte[] data = Encoding.UTF8.GetBytes("hello from netduino\n");
                socket.SendTo(data, data.Length, SocketFlags.None, new IPEndPoint(IPAddress.Parse("255.255.255.255"), 1234));
                Thread.Sleep(1000);

                socket.Close();
            }
            catch (Exception e)
            {
                Debug.Print(e.StackTrace);
                Debug.Print(e.Message);
            }
        }


        public void publish(Object o)
        {
            receiveFromLocal(o);
        }



        public void subscribe(Type t, ISubscriber subscriber)
        {
            // We care about what's happening on our node
            locallySubscribe(subscriber, t);
            // But also in the neighbourhood (we're not some kind of introvert)
            remotelySubscribe(subscriber, t);
        }



        public void unsubscribe(Type t, ISubscriber subscriber)
        {
            // same as subscribe, but the other way around
            locallyUnsubscribe(subscriber, t);
            remotelyUnsubscribe(subscriber, t);
        }
    }
}
