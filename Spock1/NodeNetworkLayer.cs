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
    public partial class Node
    {
        // CONSTANTS
        private const int UDP_PORT = 1234;
        private const int BROADCAST_MSG_MAX_SIZE = 1000;
        private const int BROADCAST_MSG_HEADER_SIZE = 1 + 4; // 1 + 4 => 1: operation, 4: sender IP
        private const int BROADCAST_MSG_MIN_SIZE = BROADCAST_MSG_HEADER_SIZE + 1;
        private const int BROADCAST_MSG_PAYLOAD_OFFSET = 5;
        private const byte UDP_COMMAND_ASKSFOR = (byte)'A';
        private const byte UDP_COMMAND_OFFERS = (byte)'O';
        private const byte UDP_COMMAND_DONTNEED = (byte)'D';

        private const int TCP_PORT = 4321;
        private const int TCP_MAX_TRIES = 5;
        private const int TCP_TIMEOUT = 30000;
        private const byte TCP_COMMAND_ACCEPT_TYPE = (byte)'A';
        private const byte TCP_COMMAND_OFFERS_TYPE = (byte)'B';
        private const byte TCP_COMMAND_OBJECT = (byte)'O';

        /**
         * Read exactly size bytes from the socket s and returns them
         */
        private byte[] readExactSize(Socket s, int size)
        {
            byte[] buffer = new byte[size];
            int receivedSize = 0;

            while (receivedSize < size)
                receivedSize += s.Receive(buffer, receivedSize, size - receivedSize, SocketFlags.None);

            return buffer;
        }


        /**
         * Broadcast
         */
        private void broadcast(byte op, byte[] payload)
        {
            try
            {
                byte[] data = new byte[BROADCAST_MSG_HEADER_SIZE + payload.Length];
                byte[] IPbytes = IPAddress.Parse(NetworkInterface.GetAllNetworkInterfaces()[0].IPAddress).GetAddressBytes();
                Debug.Assert(IPbytes.Length == 4);

                data[0] = op;                                                               // Set the opcode
                Array.Copy(IPbytes, 0, data, 1, IPbytes.Length);                            // Set the IP
                Array.Copy(payload, 0, data, BROADCAST_MSG_HEADER_SIZE, payload.Length);    // Add the payload

                Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true); // Enable broadcast
                socket.SendTo(data, data.Length, SocketFlags.None, new IPEndPoint(IPAddress.Parse("255.255.255.255"), UDP_PORT));
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
         * Returns a byte[] containing source[beginning..-1]
         */
        private byte[] getSubBytes(byte[] source, int beginning)
        {
            byte[] r = new byte[source.Length - beginning];
            Array.Copy(source, beginning, r, 0, r.Length);
            return r;
        }


        /**
         * Returns a byte[] containing source[beginning..end]
         */
        private byte[] getSubBytes(byte[] source, int beginning, int end)
        {
            byte[] r = new byte[end - beginning];
            Array.Copy(source, beginning, r, 0, r.Length);
            return r;
        }


        /**
         * Listen the broadcast requests and process them
         */
        private void listenForUDPRequest()
        {
            Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            EndPoint ep = (EndPoint)(new IPEndPoint(IPAddress.Any, UDP_PORT));
            socket.Bind(ep);

            bool stayAlive = true;
            byte[] buffer = new byte[BROADCAST_MSG_MAX_SIZE];
            while (stayAlive)
            {
                try
                {
                    int nbReceived = socket.ReceiveFrom(buffer, ref ep);
                    if (nbReceived == -1 || nbReceived < BROADCAST_MSG_MIN_SIZE) // Error receiving the datagram
                        continue;

                    string IP = buffer[1] + "." + buffer[2] + "." + buffer[3] + "." + buffer[4];
                    //String msg = new string(Encoding.UTF8.GetChars(buffer));
                    //Debug.Print("\nReceived data: " + msg + " for " + nbReceived + " B");

                    switch (buffer[0])
                    {
                        // Someone asks for a type of object
                        case UDP_COMMAND_ASKSFOR:
                            {
                                string type = new String(Encoding.UTF8.GetChars(getSubBytes(buffer, BROADCAST_MSG_HEADER_SIZE)));
                                Debug.Print(IP + " asks for the type " + type);
                                lock (typeToRemoteSubscriberLock)
                                {
                                    ArrayList currentRemotes = (ArrayList)typeToLocalSubscriber[type];

                                    if (currentRemotes == null)
                                        currentRemotes = new ArrayList();
                                    currentRemotes.Add(IP);

                                    typeToLocalSubscriber[type] = currentRemotes;
                                }

                                break;
                            }


                        // Someone offers a type of object
                        case UDP_COMMAND_OFFERS:
                            {
                                string typeName = new String(Encoding.UTF8.GetChars(getSubBytes(buffer, BROADCAST_MSG_HEADER_SIZE)));
                                Debug.Print(IP + " offers the type " + typeName);
                                lock (typeToLocalSubscriberCountLock)
                                {
                                    object count = typeToLocalSubscriberCount[typeName];
                                    if (count != null && (int)count > 0)
                                    {
                                        Debug.Print("We'll accept " + typeName);
                                        sendTCPCommand(IP, TCP_COMMAND_ACCEPT_TYPE, Encoding.UTF8.GetBytes(typeName));
                                    }
                                    else
                                        Debug.Print("We don't need " + typeName);
                                }
                                break;
                            }

                        case UDP_COMMAND_DONTNEED:
                            {
                                string typeName = new String(Encoding.UTF8.GetChars(getSubBytes(buffer, BROADCAST_MSG_HEADER_SIZE)));
                                lock (typeToRemoteSubscriberLock)
                                {
                                    ((ArrayList)typeToRemoteSubscriber[typeName]).Remove(IP);
                                }
                                Debug.Print(IP + " don't need " + typeName + " anymore");
                                break;
                            }

                        default:
                            Debug.Print("Unknown command : " + buffer[0].ToString());
                            break;
                    }
                }
                catch (Exception e)
                {
                    Debug.Print("Exception received while listening to broadcast: " + e.Message);
                    Debug.Print(e.StackTrace);
                }
                Thread.Sleep(500);
            }
        }



        /**
         * Listen for a TCP transmission, either a request or an object
         */
        private void listenForTCPRequest()
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

                        // Read the message size
                        int msgSize = (int)BitConverter.ToUInt32(readExactSize(clientSocket, sizeof(int)));
                        Debug.Print("Receiving a message of " + msgSize.ToString() + "B");

                        // Read the message itself
                        byte[] msg = readExactSize(clientSocket, msgSize);
                        string request = new string(System.Text.Encoding.UTF8.GetChars(msg));
                        Debug.Print("Message : " + request);

                        switch (msg[0])
                        {
                            case TCP_COMMAND_ACCEPT_TYPE:   // Someone needs something we got
                                {
                                    string type = new String(Encoding.UTF8.GetChars(getSubBytes(msg, 1)));
                                    Debug.Print(clientIP + " needs " + type);
                                    lock (typeToRemoteSubscriberLock)
                                    {
                                        ArrayList currentRemotes = (ArrayList)typeToRemoteSubscriber[type];

                                        if (currentRemotes == null)
                                            currentRemotes = new ArrayList();
                                        currentRemotes.Add(clientIP);

                                        typeToRemoteSubscriber[type] = currentRemotes;
                                    }
                                    break;
                                }

                            // Probably obsolete
                            case TCP_COMMAND_OFFERS_TYPE:   // Someone received our UDP demand and offers us what we need
                                {
                                    string typeName = new String(Encoding.UTF8.GetChars(getSubBytes(msg, 1)));
                                    lock (typeToLocalSubscriberLock)
                                    {
                                        if (((Array)typeToLocalSubscriber[typeName]).Length < 0)
                                            break;
                                    }
                                    sendTCPCommand(clientIP.Address.ToString(), TCP_COMMAND_ACCEPT_TYPE, Encoding.UTF8.GetBytes(type));
                                    break;
                                }

                            case TCP_COMMAND_OBJECT:        // Someone give us an object
                                {
                                    byte typeStringLen = msg[1];
                                    string typeString = new String(Encoding.UTF8.GetChars(getSubBytes(msg, 2, 2+typeStringLen)));
                                    int objectBytesBeginning = typeStringLen + 2;
                                    Debug.Print("Receiving an object of type " + typeString);
                                    Object o = null; // TODO
                                    receiveFromNetwork(o);
                                    break;
                                }

                            default:
                                Debug.Print("Received unknown TCP command : " + msg[0]);
                                break;
                        }

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


        /**
         * Package the payload then send it to the specified IP on port TCP_PORT
         */
        private void sendTCP(string destIP, byte[] payload)
        {
            int startTime = System.DateTime.Now.Millisecond;
            int sent = 0;
            int nbTries = 0;

            byte[] packet = new byte[sizeof(int) + payload.Length];
            byte[] sizeBytes = BitConverter.GetBytes(payload.Length);
            Debug.Assert(sizeBytes.Length == sizeof(int)); // Who knows... anyway, there will be a problem if one of the machines 
                                                           // has sizeof(int) != sizeof(UInt32), but should'nt be allowed according to MSDN

            Array.Copy(sizeBytes, 0, packet, 0, sizeBytes.Length);              // Add the packet's size...
            Array.Copy(payload, 0, packet, sizeBytes.Length, payload.Length);   // ...then the packet's payload

            socketSend = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socketSend.Connect(new IPEndPoint(System.Net.IPAddress.Parse(destIP), TCP_PORT));

            while (sent < packet.Length)
            {
                if (System.DateTime.Now.Millisecond > startTime + TCP_TIMEOUT)
                    return;
                try
                {
                    sent += socketSend.Send(packet, sent, packet.Length - sent, SocketFlags.None);
                }
                catch (SocketException ex)
                {
                    if (nbTries < TCP_MAX_TRIES)
                    {
                        nbTries++;
                        Thread.Sleep(30);
                    }
                    else
                    {
                        socketSend.Close();
                        throw ex;
                    }
                }
            }

            socketSend.Close();
        }


        /**
         * Send an operation
         */
        private void sendTCPCommand(string destIP, byte op, byte[] payload)
        {
            byte[] buffer = new byte[payload.Length + 1];
            buffer[0] = op;
            Array.Copy(payload, 0, buffer, 1, payload.Length);
            sendTCP(destIP, buffer);
        }


        /**
         * Send the object o to the remote client at IPAddress
         * TODO
         */
        private void sendObject(string destIP, Object o)
        {
            // Get the bytes of the serialized object
            byte[] objectData = new byte[0]; // = o.serialize ??

            // Store the name of the type
            byte[] typeName = Encoding.UTF8.GetBytes(o.GetType().Name);
            Debug.Assert(typeName.Length<256); // or increase the size in requests, but 255 should be enough for everyone ;)

            // Package the whole thing
            byte[] data = new byte[1 + typeName.Length + objectData.Length];
            data[0] = (byte)typeName.Length;
            Array.Copy(typeName, 0, data, 1, typeName.Length);
            Array.Copy(objectData, 0, data, 1+typeName.Length, objectData.Length);

            // And send it
            sendTCPCommand(destIP, TCP_COMMAND_OBJECT, data);
        }
    }
}
