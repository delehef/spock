﻿using System;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Collections;
#if MF
using Microsoft.SPOT;
using Microsoft.SPOT.Hardware;
using SecretLabs.NETMF.Hardware;
using SecretLabs.NETMF.Hardware.Netduino;
using Microsoft.SPOT.Net.NetworkInformation;
#else
using System.Net.NetworkInformation;
#endif
using System.Diagnostics;

namespace Spock
{
	/**
	  * This partial of the Node class only manage the logical part of the work
	  */
    public partial class Node
    {
        // ========== MEMBERS
        // The node singleton for this netduino
        private static readonly Node instance = new Node();

		// Dictionary {objectType: [interested remotes]}
        private readonly object typeToRemoteSubscriberLock = new object();
        private Hashtable typeToRemoteSubscriber = new Hashtable();

        // Dictionary {objectType: [local subscribers]}
        private readonly object typeToLocalSubscriberLock = new object();
        private Hashtable typeToLocalSubscriber = new Hashtable();

        Socket socketSend;     // used to send requests or objects over TCP
        Socket socketReceive;  // used to receive requests or objects over TCP


        // ========== CONSTRUCTOR
        public Node()
        {
            var networkInterfaces = NetworkInterface.GetAllNetworkInterfaces();
            Debug.Assert(networkInterfaces[0] != null);
            var net = networkInterfaces[0];

#if MF
            Debug.Print("IP Address: " + net.IPAddress);
#else
			Debug.Print("IP Address: " + net.GetPhysicalAddress());
#endif

            Thread listenThread = new Thread(new ThreadStart(listenForUDPRequest));
            listenThread.Start();

            try
            {
                socketReceive = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socketReceive.Bind(new IPEndPoint(IPAddress.Any, TCP_PORT));
                socketReceive.Listen(100); // param : size of the pending connections queue
            }
            catch (Exception e)
            {
                Debug.Print(e.StackTrace);
                Debug.Print(e.Message);
            }

            Debug.Print("TCP server launched");
            Thread TCPListenThread = new Thread(new ThreadStart(listenForTCPRequest));
            TCPListenThread.Start();
        }


        // ========== SINGLETON
        public static Node Instance
        {
            get
            {
                return instance;
            }
        }


        // ========== IMPLEMENTATION
        // ---------- PRIVATE
        /**
         * Called when a new object is received from the network
         */
        private void receiveFromNetwork(string typeName, byte[] data)
        {
            Debug.Print("We juste received a " + typeName + " from the network");

            // Transmit to the concerned locals
            Object received = Reflection.Deserialize(data, Type.GetType(typeName));
            deliverToLocals(received);
        }



        /**
         * Dictribute the object o to all the concerned locals clients
         */
        private void deliverToLocals(Object o)
        {
            string className = o.GetType().Name;
            lock (typeToLocalSubscriberLock)
            {
                ArrayList localsList = (ArrayList)(typeToLocalSubscriber[className]);
                if (localsList != null && localsList.Count != 0)
                    foreach (ISubscriber s in localsList)
                    {
                        Debug.Print("Notifying a local of a " + className);
                        s.receive(o);
                    }
            }
        }



        /**
         * Distribute the object o to all the concerned remote clients
         */
        private void deliverToRemotes(object o)
        {
			string className = o.GetType().Name;
			Debug.Print("out |"+className+"|");

            lock (typeToRemoteSubscriberLock)
            {
                ArrayList remotesList = (ArrayList)typeToRemoteSubscriber[className];
				if (remotesList != null && remotesList.Count != 0)
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
            // Transmit to the concerned local...
            deliverToLocals(o);
            // ...then to the concerned remotes
            deliverToRemotes(o);
        }



        /**
         * We got a new local subscriber for t, we have to ask for some t in the network
         */
        private void remotelySubscribe(ISubscriber subscriber, Type t)
        {
            broadcast(UDP_COMMAND_ASKSFOR, Encoding.UTF8.GetBytes(t.Name));
        }



        /**
         * Take account of a new local subscriber
         */
        private void locallySubscribe(ISubscriber subscriber, Type t)
        {
            lock (typeToLocalSubscriberLock)
            {
                ArrayList currentClients = (ArrayList)typeToLocalSubscriber[t.Name];

                if (currentClients == null)
                    currentClients = new ArrayList();
                currentClients.Add(subscriber);

                typeToLocalSubscriber[t.Name] = currentClients;
            }
        }



        /**
         * If only subscriber asks for t, we need to tell the network it's over for us
         */
        private void remotelyUnsubscribe(ISubscriber subscriber, Type t)
        {
            broadcast(UDP_COMMAND_DOESNTNEED, Encoding.UTF8.GetBytes(t.Name));
        }



        /**
         * subscriber doesn't ask for t anymore
         */
        private void locallyUnsubscribe(ISubscriber subscriber, Type t)
        {
            lock (typeToLocalSubscriberLock)
            {
                ((ArrayList)typeToLocalSubscriber[t.Name]).Remove(subscriber);
            }
        }



        // ========== PUBLIC ==========
        /**
         * What to do when a local subscriber publish an object
         */
        public void publish(Object o)
        {
			broadcast(UDP_COMMAND_OFFERS, Encoding.UTF8.GetBytes(o.GetType().Name));
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
			lock(typeToLocalSubscriberLock)
			{
			    if (((ArrayList)typeToLocalSubscriber[t.Name]).Count <= 1)
			    {
			        // same as subscribe, but the other way around
			        locallyUnsubscribe(subscriber, t);
			        remotelyUnsubscribe(subscriber, t);
			    }
			}
        }
    }
}
