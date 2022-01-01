# clonezilla-util
A tool for working with Clonezilla images.

## Extract a single file from a Clonezilla Image

Run the following command:

`clonezilla-util extract-partition-image --input <clonzilla folder> --output <folder to extract to>`

That produces the original partition images in the output folder. 

<div style="float:left;margin:100px 50px 50px 0" markdown="1">
<kbd>
<img src="https://i.imgur.com/KWpUL0J.png" width="800">
</kbd>
</div>

If the image was extracted to an NTFS drive, it is created as sparse. Meaning it only takes up the necessary space:

<img src="https://i.imgur.com/r0sepb7.png" width="300">

Now you can open the image using 7-Zip and extract the file(s) you desire.

<img src="https://i.imgur.com/enJhShq.png" width="600">
