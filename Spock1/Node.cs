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
        // The objects waiting to be published
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


        // ACCESSORS
        public static Node Instance
        {
            get
            {
                return instance;
            }
        }


        // IMPLEMENTATION
        private byte[] readExactSize(Socket s, int size)
        {
            byte[] buffer = new byte[size];
            int receivedSize = 0;

            while (receivedSize < size)
                receivedSize += s.Receive(buffer, receivedSize, size - receivedSize, SocketFlags.None);

            return buffer;
        }



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
                catch(Exception e)
                {
                    Debug.Print(e.StackTrace);
                    Debug.Print(e.Message);
                }
            }
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

            // Transmit to the concerned remotes
            lock (objectToLocalClientLock)
            {
                ArrayList remotesList = (ArrayList)objectToLocalClient[className];
                foreach (ISubscriber s in remotesList)
                    s.receive(o);
            }
        }


        /**
         * Called when a new object is received from one of the local clients
         */
        private void receiveFromLocal(Object o)
        {
            string className = o.GetType().Name;
            Debug.Print("We juste received a " + className + " from the local client");

            // Transmit to the concerned remotes
            lock (objectToRemoteClientLock)
            {
                ArrayList remotesList = (ArrayList)objectToRemoteClient[className];
                foreach (string address in remotesList)
                    sendObject(address, o);
            }
        }


        public static void Main()
        {
            Node node = new Node();
        }
    }
}
