# Semantic Mail

This repository contains a project of mine which aims to solve the struggle of email search. Primarily Outlook since this is what I personally use, but possibly more clients in the future.

## How it Works

Semantic mail works on a simple concept; a mail client with local vector embeddings of emails. This enables the search to find not just exact matches, but also similar phrases or near hits. 

For example, a user looking for information on their upcoming kick-off agenda, might be looking for the phrase "kick-off schedule", but if the email had a typo in kickoff and used the word agenda instead, outlook would completely miss it. By storing the vectors of the email, we can find it on similarity instead.

## Architecture

This is a fully local project. Your data never leaves your machine. Your emails are pre-loaded onto your computer where embeddings are generated and stored. These are then locally compared to produce a result that can be matched by similarity, point-in-time, and other flags like has-attachments, etc.

Emails are fetched using Microsoft Graph and users are authenticated using Entra ID.

## Goals

At the current stage of the project- we are just aiming to solve the problem of finding emails. Actually composing them is something we still defer to normal outlook clients / web-apps.

In the first iteration, this will simply be a console application that lets us evaluate how well a semantic search engine will work for emails and which pre-proccessing needs to be done in order to turn this into a proper product.